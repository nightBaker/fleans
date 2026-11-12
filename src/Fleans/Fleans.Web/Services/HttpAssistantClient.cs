using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text.Json;
using AGUI.Abstractions;
using Fleans.Assistant.Contracts;
using Microsoft.Extensions.Options;

namespace Fleans.Web.Services;

/// <summary>What one AG-UI run answered: the streamed text, the proposal if any, or the error that ended it.</summary>
public sealed record AssistantReply(string Text, string? ProposalXml, AssistantError? Error);

/// <summary>One thing the backend reports alongside its answer, or between requests (#1017): exactly one of these is set.</summary>
public sealed record AssistantProgress(
    AssistantActivity? Activity = null,
    AssistantApprovalRequest? Approval = null,
    AssistantJobUpdate? Job = null);

/// <summary>
/// Calls the configured assistant backend (#1015, <see cref="AssistantOptions"/>): runs its AG-UI agent endpoint (#1054)
/// and reads the AG-UI event stream (<see cref="AgUiEventReader"/>), plus the stored-chat, event-feed and job calls of backends that keep chats (#1017). The assistant backend is outside the cluster, so — like
/// <see cref="EnvironmentClient"/> — it is one of the Admin UI's HTTP exceptions. With
/// authentication on, <see cref="AssistantTokenHandler"/> forwards the signed-in user's access token.
/// Failures never throw: they come back as an <see cref="AssistantError"/> the panel shows.
/// </summary>
public sealed partial class HttpAssistantClient(
    IHttpClientFactory httpClientFactory,
    IOptions<AssistantOptions> options,
    ILogger<HttpAssistantClient> logger)
{
    public const string HttpClientName = "fleans-assistant";

    /// <param name="onText">Called with each piece of assistant text as it arrives.</param>
    /// <param name="onProgress">Called with each tool call, approval and job update as it arrives.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (the user stopped it).</exception>
    public async Task<AssistantReply> SendAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct,
        Func<AssistantProgress, Task>? onProgress = null)
    {
        var started = Stopwatch.GetTimestamp();
        LogCalling(request.Messages.Count, request.CurrentXml is not null, request.Plugins.Count);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.Timeout);
        var reader = new AgUiEventReader();
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, EndpointUri(AssistantProtocol.AgentPath))
            {
                Content = JsonContent.Create(AssistantAgUi.ToRunAgentInput(request), AGUIJsonSerializerContext.Default.RunAgentInput),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Failed(reader, await ReadHttpErrorAsync(response, timeout.Token), (int)response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
            {
                if (AgUiEventReader.Parse(item.Data) is not { } evt)
                    continue;
                var (delta, progress) = reader.Read(evt);
                if (delta.Length > 0)
                    await onText(delta);
                if (onProgress is not null)
                    foreach (var p in progress)
                        await onProgress(p);
                if (reader.Error is { } error)
                    return Failed(reader, error, null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(reader, new AssistantError(AssistantErrorCodes.Timeout,
                $"The assistant did not finish within {options.Value.Timeout.TotalSeconds:0} s."), null);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(ex);
            return Failed(reader, new AssistantError(AssistantErrorCodes.Unavailable, "The assistant could not be reached."), null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException)
        {
            LogUnreadable(ex);
            return Failed(reader, new AssistantError(AssistantErrorCodes.BadResponse, "The assistant's response could not be read."), null);
        }

        LogAnswered((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, reader.Text.Length, reader.Proposal is not null);
        return new AssistantReply(reader.Text.ToString(), reader.Proposal, null);
    }

    /// <summary>
    /// <c>GET /assistant/capabilities</c>. A backend without it (404) only serves the chat call: that is an
    /// answer with no features. <c>null</c> when the backend could not tell (unreachable, 5xx, …), so the
    /// caller may ask again later.
    /// </summary>
    public async Task<AssistantCapabilities?> GetCapabilitiesAsync(CancellationToken ct)
    {
        var (capabilities, error, status) = await CallAsync<AssistantCapabilities>(HttpMethod.Get, AssistantProtocol.CapabilitiesPath, ct);
        if (status == (int)HttpStatusCode.NotFound)
            return new AssistantCapabilities(1, []);
        return error is null ? capabilities : null;
    }

    /// <summary><c>POST /assistant/conversations</c>: a new stored chat (#1017).</summary>
    public async Task<(AssistantConversation? Conversation, AssistantError? Error)> CreateConversationAsync(CancellationToken ct)
    {
        var (conversation, error, _) = await CallAsync<AssistantConversation>(HttpMethod.Post, AssistantProtocol.ConversationsPath, ct);
        return (conversation, error);
    }

    /// <summary><c>GET /assistant/conversations</c>: the user's stored chats, most recently updated first.</summary>
    /// <param name="cursor">The previous page's <see cref="AssistantConversationPage.NextCursor"/>; <c>null</c> for the first page.</param>
    public async Task<(AssistantConversationPage? Page, AssistantError? Error)> ListConversationsAsync(CancellationToken ct, string? cursor = null)
    {
        var path = cursor is null ? AssistantProtocol.ConversationsPath : AssistantProtocol.ConversationsPath + "?cursor=" + Uri.EscapeDataString(cursor);
        var (page, error, _) = await CallAsync<AssistantConversationPage>(HttpMethod.Get, path, ct);
        return (page, error);
    }

    /// <summary><c>GET /assistant/conversations/{id}</c>: a stored chat's turns, oldest first.</summary>
    public async Task<(AssistantConversationHistory? History, AssistantError? Error)> GetConversationAsync(string conversationId, CancellationToken ct)
    {
        var (history, error, _) = await CallAsync<AssistantConversationHistory>(HttpMethod.Get, ConversationPath(conversationId), ct);
        return (history, error);
    }

    /// <summary><c>DELETE /assistant/conversations/{id}</c>; <c>null</c> on success.</summary>
    public async Task<AssistantError?> DeleteConversationAsync(string conversationId, CancellationToken ct)
    {
        var (_, error, _) = await CallAsync<object>(HttpMethod.Delete, ConversationPath(conversationId), ct);
        return error;
    }

    /// <summary><c>PATCH /assistant/conversations/{id}</c>; <c>null</c> on success.</summary>
    public async Task<AssistantError?> RenameConversationAsync(string conversationId, string title, CancellationToken ct)
    {
        var (_, error, _) = await CallAsync<object>(HttpMethod.Patch, ConversationPath(conversationId), ct, new AssistantConversationUpdate(title));
        return error;
    }

    /// <summary><c>POST /assistant/jobs/{id}/cancel</c>; <c>null</c> when the backend took the request. The outcome arrives as a <c>job</c> event.</summary>
    public async Task<AssistantError?> CancelJobAsync(string jobId, CancellationToken ct)
    {
        var (_, error, _) = await CallAsync<object>(HttpMethod.Post, $"{AssistantProtocol.JobsPath}/{Uri.EscapeDataString(jobId)}/cancel", ct);
        return error;
    }

    /// <summary>
    /// One connection to <c>GET /assistant/conversations/{id}/events</c>, read until the backend ends it. It lasts at most
    /// <see cref="AssistantOptions.Timeout"/>, so a connection that silently died is replaced; the next one resumes from the last id.
    /// </summary>
    /// <param name="lastEventId">Sent as <c>Last-Event-ID</c>; <c>null</c> reads the chat's events from the start.</param>
    /// <param name="onEvent">Called with each event's id and, for an event the editor uses, what it reports (once per
    /// approval when one <c>RUN_FINISHED</c> carries several interrupts).</param>
    /// <returns><c>false</c> when reconnecting is pointless: the token was rejected, or this chat or backend has no feed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<bool> ReadEventsAsync(string conversationId, string? lastEventId, Func<string?, AssistantProgress?, Task> onEvent, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.Timeout);
        try
        {
            var path = $"{ConversationPath(conversationId)}/{AssistantProtocol.ConversationEventsSegment}";
            using var message = new HttpRequestMessage(HttpMethod.Get, EndpointUri(path));
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (lastEventId is not null)
                message.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            LogStoredChatCall(HttpMethod.Get.Method, ResourceOf(path), (int)response.StatusCode);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                return false;
            if (!response.IsSuccessStatusCode)
                return true;

            var reader = new AgUiEventReader();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
            {
                List<AssistantProgress> progress = [];
                try
                {
                    if (AgUiEventReader.Parse(item.Data) is { } evt)
                        progress = reader.Read(evt).Progress;
                }
                catch (JsonException ex)
                {
                    // Its id still moves the position: asking again would only get the same event back.
                    LogUnreadable(ex);
                }
                if (progress.Count == 0)
                    await onEvent(item.EventId, null);
                foreach (var p in progress)
                    await onEvent(item.EventId, p);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The connection's time is up: not a failure.
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(ex);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            LogUnreadable(ex);
        }
        return true;
    }

    private static string ConversationPath(string conversationId) =>
        AssistantProtocol.ConversationsPath + "/" + Uri.EscapeDataString(conversationId);

    /// <summary>One JSON call besides the chat. Failures come back as an <see cref="AssistantError"/>, never thrown;
    /// <c>Status</c> is the HTTP status when the backend answered.</summary>
    private async Task<(T? Value, AssistantError? Error, int? Status)> CallAsync<T>(HttpMethod method, string path, CancellationToken ct, object? body = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.Timeout);
        try
        {
            using var message = new HttpRequestMessage(method, EndpointUri(path));
            if (body is not null)
                message.Content = JsonContent.Create(body, body.GetType(), options: AssistantProtocol.JsonOptions);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, timeout.Token);
            var status = (int)response.StatusCode;
            LogStoredChatCall(method.Method, ResourceOf(path), status);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadHttpErrorAsync(response, timeout.Token);
                // A 404 is an ordinary answer here: a backend without capabilities, a chat or a file that is gone.
                if (response.StatusCode != HttpStatusCode.NotFound)
                    LogFailed(error.Code, status);
                return (default, error, status);
            }
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return (default, null, status);
            var value = await response.Content.ReadFromJsonAsync<T>(AssistantProtocol.JsonOptions, timeout.Token)
                ?? throw new JsonException($"Empty {typeof(T).Name} response.");
            return (value, null, status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (default, new AssistantError(AssistantErrorCodes.Timeout,
                $"The assistant did not answer within {options.Value.Timeout.TotalSeconds:0} s."), null);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(ex);
            return (default, new AssistantError(AssistantErrorCodes.Unavailable, "The assistant could not be reached."), null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
        {
            LogUnreadable(ex);
            return (default, new AssistantError(AssistantErrorCodes.BadResponse, "The assistant's response could not be read."), null);
        }
    }

    // What the debug log names: never the query, which holds what the user searched for.
    private static string ResourceOf(string path) =>
        path.StartsWith(AssistantProtocol.KnowledgePath + "/", StringComparison.Ordinal)
            ? "knowledge/" + path[(AssistantProtocol.KnowledgePath.Length + 1)..].Split('?')[0]
            : path.Split('/')[1];

    private Uri EndpointUri(string path)
    {
        var endpoint = options.Value.Endpoint!;
        return new Uri(new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/"), path);
    }

    private AssistantReply Failed(AgUiEventReader reader, AssistantError error, int? statusCode)
    {
        LogFailed(error.Code, statusCode);
        return new AssistantReply(reader.Text.ToString(), null, error);
    }

    private static async Task<AssistantError> ReadHttpErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => AssistantErrorCodes.Unauthorized,
            HttpStatusCode.TooManyRequests => AssistantErrorCodes.RateLimited,
            _ when status >= 500 => AssistantErrorCodes.Unavailable,
            _ => AssistantErrorCodes.BadResponse,
        };

        // A backend may explain itself with an AssistantError body.
        AssistantError? body = null;
        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(json))
                body = JsonSerializer.Deserialize<AssistantError>(json, AssistantProtocol.JsonOptions);
        }
        catch (JsonException)
        {
        }
        // A stored chat that is gone is the one 404 the editor acts on (it starts a new chat).
        if (response.StatusCode == HttpStatusCode.NotFound && body?.Code == AssistantErrorCodes.ConversationNotFound)
            code = AssistantErrorCodes.ConversationNotFound;
        var message = body?.Message;
        return new AssistantError(code, string.IsNullOrWhiteSpace(message) ? $"The assistant answered HTTP {status}." : message);
    }

    // Prompt and diagram contents are never logged — only their shape.
    [LoggerMessage(EventId = 13070, Level = LogLevel.Information,
        Message = "Calling the assistant: {MessageCount} messages, diagram sent {HasDiagram}, {PluginCount} plugins")]
    private partial void LogCalling(int messageCount, bool hasDiagram, int pluginCount);

    [LoggerMessage(EventId = 13071, Level = LogLevel.Information,
        Message = "The assistant answered in {ElapsedMs} ms: {TextLength} characters, proposal {HasProposal}")]
    private partial void LogAnswered(long elapsedMs, int textLength, bool hasProposal);

    [LoggerMessage(EventId = 13072, Level = LogLevel.Warning,
        Message = "The assistant call failed with {Code} (HTTP {StatusCode})")]
    private partial void LogFailed(string code, int? statusCode);

    [LoggerMessage(EventId = 13073, Level = LogLevel.Warning, Message = "The assistant backend could not be reached")]
    private partial void LogUnreachable(Exception ex);

    [LoggerMessage(EventId = 13074, Level = LogLevel.Warning, Message = "The assistant's event stream could not be read")]
    private partial void LogUnreadable(Exception ex);

    [LoggerMessage(EventId = 13079, Level = LogLevel.Debug,
        Message = "Assistant {Method} {Resource} answered HTTP {StatusCode}")]
    private partial void LogStoredChatCall(string method, string resource, int statusCode);
}
