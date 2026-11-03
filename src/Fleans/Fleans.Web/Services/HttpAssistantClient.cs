using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Fleans.Assistant.Contracts;
using Microsoft.Extensions.Options;

namespace Fleans.Web.Services;

/// <summary>What one <c>POST /assistant/chat</c> answered: the streamed text, the proposal if any,
/// or the error that ended it.</summary>
public sealed record AssistantReply(string Text, string? ProposalXml, AssistantError? Error);

/// <summary>
/// Calls the configured assistant backend (#1015, <see cref="AssistantOptions"/>) and reads its
/// <c>text/event-stream</c> answer. The assistant backend is outside the cluster, so — like
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

    /// <param name="onText">Called with each <c>text</c> event as it arrives.</param>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (the user stopped it).</exception>
    public async Task<AssistantReply> SendAsync(AssistantRequest request, Func<string, Task> onText, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        LogCalling(request.Messages.Count, request.CurrentXml is not null, request.Plugins.Count);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Value.Timeout);
        var text = new StringBuilder();
        string? proposal = null;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, ChatUri())
            {
                Content = JsonContent.Create(request, options: AssistantProtocol.JsonOptions),
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Failed(text, await ReadHttpErrorAsync(response, timeout.Token), (int)response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(timeout.Token))
            {
                switch (item.EventType)
                {
                    case AssistantProtocol.TextEvent:
                        var delta = Deserialize<AssistantTextDelta>(item.Data).Text ?? "";
                        text.Append(delta);
                        await onText(delta);
                        break;
                    case AssistantProtocol.ProposalEvent:
                        // At most one per response; a second one is a backend bug — keep the first.
                        proposal ??= Deserialize<AssistantProposal>(item.Data).BpmnXml ?? "";
                        break;
                    case AssistantProtocol.ErrorEvent:
                        return Failed(text, Deserialize<AssistantError>(item.Data), null);
                    // Unknown event types are skipped, so a backend may add its own.
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed(text, new AssistantError(AssistantErrorCodes.Timeout,
                $"The assistant did not finish within {options.Value.Timeout.TotalSeconds:0} s."), null);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(ex);
            return Failed(text, new AssistantError(AssistantErrorCodes.Unavailable, "The assistant could not be reached."), null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException)
        {
            LogUnreadable(ex);
            return Failed(text, new AssistantError(AssistantErrorCodes.BadResponse, "The assistant's response could not be read."), null);
        }

        LogAnswered((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, text.Length, proposal is not null);
        return new AssistantReply(text.ToString(), proposal, null);
    }

    private Uri ChatUri()
    {
        var endpoint = options.Value.Endpoint!;
        return new Uri(new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/"), AssistantProtocol.ChatPath);
    }

    private AssistantReply Failed(StringBuilder text, AssistantError error, int? statusCode)
    {
        LogFailed(error.Code, statusCode);
        return new AssistantReply(text.ToString(), null, error);
    }

    private static T Deserialize<T>(string data) =>
        JsonSerializer.Deserialize<T>(data, AssistantProtocol.JsonOptions)
            ?? throw new JsonException($"Empty {typeof(T).Name} event.");

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
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
                message = JsonSerializer.Deserialize<AssistantError>(body, AssistantProtocol.JsonOptions)?.Message;
        }
        catch (JsonException)
        {
        }
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
}
