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
        if (!string.IsNullOrEmpty(body))
        {
            var contentType = contentTypeHeader.Key is null ? DefaultRequestContentType : contentTypeHeader.Value;
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;  // already handled via StringContent
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var bodyLength = body?.Length ?? 0;
        LogSendingRequest(method, uri.ToString(), headers.Count, bodyLength, timeoutSec, context.ActivityInstanceId);
        var stopwatch = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            stopwatch.Stop();
            LogTimeout(method, uri.ToString(), timeoutSec, stopwatch.ElapsedMilliseconds);
            throw new CustomTaskFailedActivityException("504", $"timeout after {timeoutSec}s calling {uri}");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            LogNetworkError(ex, method, uri.ToString(), stopwatch.ElapsedMilliseconds);
            throw new CustomTaskFailedActivityException("502", $"network error calling {uri}: {ex.Message}");
        }
        stopwatch.Stop();

        try
        {
            var statusCode = (int)response.StatusCode;
            var contentLength = response.Content.Headers.ContentLength ?? -1;
            LogResponseReceived(method, uri.ToString(), statusCode, contentLength, stopwatch.ElapsedMilliseconds);
            return await BuildResponseAsync(response, successCodes, uri, method);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<IDictionary<string, object?>> BuildResponseAsync(
        HttpResponseMessage response, IReadOnlyList<int>? successCodes, Uri uri, string method)
    {
        var statusCode = (int)response.StatusCode;
        var rawBody = await response.Content.ReadAsStringAsync();
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

        var isSuccess = successCodes is { Count: > 0 }
            ? successCodes.Contains(statusCode)
            : statusCode is >= 200 and <= 299;

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

    private static string? ReadString(IDictionary<string, object?> inputs, string key)
        => inputs.TryGetValue(key, out var v) ? v?.ToString() : null;

    private static int? ReadInteger(IDictionary<string, object?> inputs, string key)
    {
        if (!inputs.TryGetValue(key, out var v) || v is null) return null;
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
}
