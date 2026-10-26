using System.Diagnostics;
using System.Dynamic;
using System.Net.Http.Headers;
using System.Text;
using Fleans.Application.Abstractions.Events;
using Fleans.Domain.Errors;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Fleans.Plugins.RestCaller;

/// <summary>
/// Backs <c>&lt;serviceTask type="rest-call"&gt;</c>. Issues an HTTP request per the
/// resolved input parameters; populates <c>__response</c> with status / statusCode / ok /
/// body / headers; throws a typed <see cref="CustomTaskFailedActivityException"/> on
/// non-success per the v2 design's failure-code mapping.
///
/// <c>[ImplicitStreamSubscription]</c> carries the per-<c>TaskType</c> namespace literal
/// (<c>events.ExecuteCustomTaskEvent.rest-call</c>) — attribute arguments must be
/// compile-time constants, so each plugin subclass declares its own string. The string
/// MUST match <see cref="WorkflowEventStreams.GetExecuteCustomTaskNamespace"/> applied
/// to <see cref="TaskType"/>; <c>AddCustomTaskPlugin&lt;RestCallerHandler&gt;("rest-call", …)</c>
/// validates this at silo startup and throws on drift.
///
/// <c>[WorkerPlacement]</c> is intentionally NOT applied: plugin grains rely on Orleans
/// default placement so that <c>GetCompatibleSilos</c> (assembly-loading based) is the
/// sole isolation primitive for routing plugin handlers to the silo that actually has
/// the plugin's DLL loaded.
/// </summary>
[GrainType("custom-task.rest-call")]
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.rest-call")]
public sealed partial class RestCallerHandler : CustomTaskHandlerBase
{
    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"
    };

    // TypeNameHandling.None — response bodies are untrusted external data; allowing $type
    // would be a real CVE. Default Newtonsoft setting is None; we set it explicitly to keep
    // a future contributor from "helpfully" enabling TypeNameHandling.All.
    private static readonly JsonSerializerSettings ResponseDeserializerSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
    };

    private const string DefaultRequestContentType = "application/json";
    private const int FailureBodyTruncate = 1024;

    private readonly HttpClient _http;
    private readonly ILogger<RestCallerHandler> _logger;

    public RestCallerHandler(HttpClient http, ILogger<RestCallerHandler> logger, IGrainFactory grainFactory)
        : base(logger, grainFactory)
    {
        _http = http;
        _logger = logger;
    }

    protected override string TaskType => "rest-call";

    protected override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        var url = ReadString(resolvedInputs, "url")
            ?? throw new CustomTaskFailedActivityException("400", "url is required");
        var method = (ReadString(resolvedInputs, "method") ?? "GET").ToUpperInvariant();
        if (!AllowedMethods.Contains(method))
            throw new CustomTaskFailedActivityException("400", $"unsupported method '{method}'");

        Uri uri;
        try { uri = new Uri(url); }
        catch (UriFormatException ex)
        {
            throw new CustomTaskFailedActivityException("400", $"invalid url '{url}': {ex.Message}");
        }

        var timeoutSec = ReadInteger(resolvedInputs, "timeoutSec")
            ?? throw new CustomTaskFailedActivityException("400", "timeoutSec is required");
        if (timeoutSec is < 1 or > 300)
            throw new CustomTaskFailedActivityException("400", $"timeoutSec must be in [1, 300]; got {timeoutSec}");

        var headers = ReadMap(resolvedInputs, "headers");
        var body = ReadString(resolvedInputs, "body");
        var successCodes = ReadIntegerList(resolvedInputs, "successCodes");
        var idempotencyKeyHeader = ReadString(resolvedInputs, "idempotencyKeyHeader");

        // Per-task body cap may only LOWER the silo ceiling (the typed client's
        // MaxResponseContentBufferSize, 8 MiB unless the host overrides it) — never raise it.
        var ceiling = _http.MaxResponseContentBufferSize;
        var requestedMax = ReadInteger(resolvedInputs, "maxResponseBytes");
        if (requestedMax is < 1)
            throw new CustomTaskFailedActivityException("400", $"maxResponseBytes must be >= 1; got {requestedMax}");
        var maxResponseBytes = requestedMax is { } m ? Math.Min(m, ceiling) : ceiling;

        // Idempotency-key opt-in. Plugin's value wins on collision with user-supplied headers.
        if (!string.IsNullOrWhiteSpace(idempotencyKeyHeader))
        {
            headers[idempotencyKeyHeader] = context.ActivityInstanceId.ToString();
            LogIdempotencyKeySet(idempotencyKeyHeader, context.ActivityInstanceId);
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);

        // Set body + Content-Type before adding other headers so a user-supplied Content-Type override wins.
        var contentTypeHeader = headers.FirstOrDefault(kv =>
            string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase));
        // Validate a user-supplied Content-Type up front: the header loop below skips it, and
        // StringContent's media-type parser would otherwise throw an unhandled FormatException.
        if (contentTypeHeader.Key is not null)
            ValidateHeader(contentTypeHeader.Key, contentTypeHeader.Value);
        if (!string.IsNullOrEmpty(body))
        {
            var contentType = contentTypeHeader.Key is null ? DefaultRequestContentType : contentTypeHeader.Value;
            try
            {
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }
            catch (FormatException)
            {
                throw new CustomTaskFailedActivityException("400",
                    $"header 'Content-Type' value '{contentType}' is not a valid media type");
            }
        }

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;  // already handled via StringContent
            ValidateHeader(name, value);
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var bodyLength = body?.Length ?? 0;
        LogSendingRequest(method, uri.ToString(), headers.Count, bodyLength, timeoutSec, context.ActivityInstanceId);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // ResponseHeadersRead: HttpClient.MaxResponseContentBufferSize is fixed per client and only
            // enforced under ResponseContentRead, so the body is bounded by ReadBodyAsync instead — that
            // is what lets maxResponseBytes vary per task. Never read response.Content outside
            // ReadBodyAsync: an unbuffered read here has no size limit.
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            var statusCode = (int)response.StatusCode;
            var isSuccess = successCodes is { Count: > 0 }
                ? successCodes.Contains(statusCode)
                : statusCode is >= 200 and <= 299;

            // HEAD / 204 / 205 / 304 carry no body, but may still declare a Content-Length.
            var rawBody = method == "HEAD" || statusCode is 204 or 205 or 304
                ? string.Empty
                : await ReadBodyAsync(response.Content, maxResponseBytes, isSuccess, statusCode, method, uri, linkedCts.Token);
            // Logged after the body read so the elapsed time includes the transfer.
            LogResponseReceived(method, uri.ToString(), statusCode,
                response.Content.Headers.ContentLength ?? -1, stopwatch.ElapsedMilliseconds);

            return BuildResponse(response, rawBody, isSuccess, uri, method);
        }
        // The body is read outside SendAsync, where a cancelled socket read surfaces as
        // IOException / HttpRequestException rather than OperationCanceledException — so classify
        // by which token fired, not by exception type.
        catch (Exception ex) when (IsTransportOrCancel(ex) && cancellationToken.IsCancellationRequested)
        {
            // The activity itself was cancelled: propagate as cancellation, never as a failure.
            if (ex is OperationCanceledException) throw;
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex) when (IsTransportOrCancel(ex) && timeoutCts.IsCancellationRequested)
        {
            LogTimeout(method, uri.ToString(), timeoutSec, stopwatch.ElapsedMilliseconds);
            throw new CustomTaskFailedActivityException("504", $"timeout after {timeoutSec}s calling {uri}");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            LogNetworkError(ex, method, uri.ToString(), stopwatch.ElapsedMilliseconds);
            throw new CustomTaskFailedActivityException("502", $"network error calling {uri}: {ex.Message}");
        }
    }

    private static bool IsTransportOrCancel(Exception ex) =>
        ex is OperationCanceledException or HttpRequestException or IOException;

    /// <summary>
    /// Buffers the response body up to <paramref name="maxBytes"/> and decodes it exactly as
    /// <c>ReadAsStringAsync</c> does. Over the limit, a success response fails with <c>502</c>; a
    /// non-success response keeps its status code (so boundary error events still route on it) and
    /// gets a placeholder body for the failure message.
    /// </summary>
    private async Task<string> ReadBodyAsync(
        HttpContent content, long maxBytes, bool isSuccess, int statusCode, string method, Uri uri, CancellationToken ct)
    {
        try
        {
            // Rejects a declared Content-Length over the limit before reading, and stops a
            // chunked body as soon as it crosses the limit.
            await content.LoadIntoBufferAsync(maxBytes, ct);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            LogResponseTooLarge(method, uri.ToString(), statusCode, content.Headers.ContentLength ?? maxBytes + 1, maxBytes);
            var message = $"response body from {uri} exceeds limit of {maxBytes} bytes";
            if (isSuccess)
                throw new CustomTaskFailedActivityException("502", message);
            return $"({message})";
        }
        return await content.ReadAsStringAsync(ct);
    }

    private IDictionary<string, object?> BuildResponse(
        HttpResponseMessage response, string rawBody, bool isSuccess, Uri uri, string method)
    {
        var statusCode = (int)response.StatusCode;
        var contentTypeStr = response.Content.Headers.ContentType?.ToString();
        var bodyShape = TryParseJsonBody(rawBody, response.Content.Headers.ContentType);

        var responseHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in response.Headers)
            responseHeaders[h.Key] = string.Join(",", h.Value);
        foreach (var h in response.Content.Headers)
            responseHeaders[h.Key] = string.Join(",", h.Value);

        var responseExpando = new ExpandoObject();
        var responseDict = (IDictionary<string, object?>)responseExpando;
        responseDict["status"] = statusCode;
        responseDict["statusCode"] = statusCode;
        responseDict["ok"] = response.IsSuccessStatusCode;
        responseDict["body"] = bodyShape;
        responseDict["headers"] = responseHeaders;
        responseDict["contentType"] = contentTypeStr;

        if (!isSuccess)
        {
            LogNonSuccessStatus(method, uri.ToString(), statusCode);
            var truncated = rawBody.Length <= FailureBodyTruncate
                ? rawBody
                : rawBody[..FailureBodyTruncate] + "…";
            throw new CustomTaskFailedActivityException(statusCode.ToString(),
                $"HTTP {statusCode} from {uri}: {truncated}");
        }

        return new Dictionary<string, object?> { ["__response"] = responseExpando };
    }

    private object? TryParseJsonBody(string rawBody, MediaTypeHeaderValue? contentType)
    {
        if (string.IsNullOrEmpty(rawBody)) return rawBody;
        if (contentType?.MediaType is null) return rawBody;

        var media = contentType.MediaType;
        var isJson = string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase)
            || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        if (!isJson) return rawBody;

        try { return JsonConvert.DeserializeObject<ExpandoObject>(rawBody, ResponseDeserializerSettings); }
        catch (JsonException ex)
        {
            LogJsonParseFailed(ex, contentType.MediaType ?? "(none)");
            return rawBody;
        }
    }

    // --- helpers for resolvedInputs coercion ---

    /// <summary>
    /// Rejects header names/values that contain CR or LF. Modern .NET handlers strip
    /// these at send time and raise HttpRequestException, but rejecting here makes
    /// the failure deterministic and produces a 400 CustomTaskFailedActivityException
    /// instead of a 502 network error wrapper. Header names additionally must conform
    /// to RFC 7230 token grammar — `TryAddWithoutValidation` lets through bytes that
    /// silently break the upstream parser; reject up-front so workflow authors get a
    /// clear error.
    /// </summary>
    private static void ValidateHeader(string name, string value)
    {
        if (string.IsNullOrEmpty(name))
            throw new CustomTaskFailedActivityException("400", "header name cannot be empty");

        if (ContainsCrOrLf(name))
            throw new CustomTaskFailedActivityException("400",
                $"header name '{name}' contains CR or LF");

        foreach (var c in name)
        {
            // RFC 7230 token: !#$%&'*+-.^_`|~ and DIGIT / ALPHA. Excludes control chars,
            // whitespace, separators (:,;()<>@\"/[]?={}). Conservative on purpose —
            // headers that don't fit this grammar would be rejected by the SocketsHttpHandler
            // anyway; failing early avoids ambiguous 502 wrappers.
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                  || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-'
                       or '.' or '^' or '_' or '`' or '|' or '~';
            if (!ok)
                throw new CustomTaskFailedActivityException("400",
                    $"header name '{name}' contains invalid character '{c}' " +
                    "(RFC 7230 token grammar required)");
        }

        if (ContainsCrOrLf(value))
            throw new CustomTaskFailedActivityException("400",
                $"header '{name}' value contains CR or LF");
    }

    private static bool ContainsCrOrLf(string s)
    {
        for (var i = 0; i < s.Length; i++)
            if (s[i] is '\r' or '\n') return true;
        return false;
    }

    private static string? ReadString(IDictionary<string, object?> inputs, string key)
        => inputs.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int? ReadInteger(IDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null) return null;
        if (v is string str && string.IsNullOrWhiteSpace(str)) return null;  // blank editor field = absent
        try { return Convert.ToInt32(v); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new CustomTaskFailedActivityException("400",
                $"input '{key}' must be an integer; got {v} ({v.GetType().Name})");
        }
    }

    private static IReadOnlyList<int>? ReadIntegerList(IDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null) return null;
        if (v is System.Collections.IEnumerable enumerable && v is not string)
        {
            var result = new List<int>();
            foreach (var item in enumerable)
            {
                if (item is null) continue;
                try { result.Add(Convert.ToInt32(item)); }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    throw new CustomTaskFailedActivityException("400",
                        $"input '{key}' must be a list of integers; got entry {item} ({item.GetType().Name})");
                }
            }
            return result;
        }
        throw new CustomTaskFailedActivityException("400",
            $"input '{key}' must be a list of integers; got {v.GetType().Name}");
    }

    private static Dictionary<string, string> ReadMap(IDictionary<string, object?> inputs, string key)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!inputs.TryGetValue(key, out var v) || v is null) return map;

        if (v is IDictionary<string, object?> dict)
        {
            foreach (var (k, value) in dict)
            {
                if (value is null) continue;            // skip null per design (don't send empty header)
                if (value is string or bool or int or long or double or decimal)
                    map[k] = value.ToString()!;
                // complex values (nested object/list) are intentionally skipped + warned below
            }
            return map;
        }

        throw new CustomTaskFailedActivityException("400",
            $"input '{key}' must be a map of strings; got {v.GetType().Name}");
    }

    // --- structured logging via [LoggerMessage] source generators ---
    // Bodies are NEVER logged: they may contain secrets or PII. Counts and lengths only.

    [LoggerMessage(EventId = 9340, Level = LogLevel.Information,
        Message = "REST plugin sending {Method} {Url} (headers={HeaderCount}, body={BodyLength}B, timeout={TimeoutSec}s, activityInstanceId={ActivityInstanceId})")]
    private partial void LogSendingRequest(string method, string url, int headerCount, int bodyLength, int timeoutSec, Guid activityInstanceId);

    [LoggerMessage(EventId = 9341, Level = LogLevel.Information,
        Message = "REST plugin received {StatusCode} for {Method} {Url} ({ContentLength}B, {ElapsedMs}ms)")]
    private partial void LogResponseReceived(string method, string url, int statusCode, long contentLength, long elapsedMs);

    [LoggerMessage(EventId = 9342, Level = LogLevel.Warning,
        Message = "REST plugin timed out after {TimeoutSec}s calling {Method} {Url} (elapsed={ElapsedMs}ms)")]
    private partial void LogTimeout(string method, string url, int timeoutSec, long elapsedMs);

    [LoggerMessage(EventId = 9343, Level = LogLevel.Warning,
        Message = "REST plugin network error on {Method} {Url} (elapsed={ElapsedMs}ms)")]
    private partial void LogNetworkError(Exception ex, string method, string url, long elapsedMs);

    [LoggerMessage(EventId = 9344, Level = LogLevel.Warning,
        Message = "REST plugin received non-success status {StatusCode} from {Method} {Url} — failing the activity (configure successCodes input to whitelist this code)")]
    private partial void LogNonSuccessStatus(string method, string url, int statusCode);

    [LoggerMessage(EventId = 9345, Level = LogLevel.Debug,
        Message = "REST plugin set idempotency-key header '{HeaderName}' = activityInstanceId={ActivityInstanceId}")]
    private partial void LogIdempotencyKeySet(string headerName, Guid activityInstanceId);

    [LoggerMessage(EventId = 9346, Level = LogLevel.Warning,
        Message = "REST plugin failed to deserialize response body as JSON despite Content-Type='{ContentType}'; body returned as raw string")]
    private partial void LogJsonParseFailed(Exception ex, string contentType);

    [LoggerMessage(EventId = 9347, Level = LogLevel.Warning,
        Message = "REST plugin response {StatusCode} from {Method} {Url}: body of at least {BodyBytes}B exceeds limit {MaxBytes}B — body not read (success status fails with 502, otherwise the status code is kept)")]
    private partial void LogResponseTooLarge(string method, string url, int statusCode, long bodyBytes, long maxBytes);
}
