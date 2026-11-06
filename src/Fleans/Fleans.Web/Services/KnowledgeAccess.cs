using Fleans.Assistant.Contracts;
using Microsoft.Extensions.Options;

namespace Fleans.Web.Services;

/// <summary>
/// Whether this circuit's user gets the Knowledge tab (#1025): the assistant is configured and its capabilities
/// list <c>knowledge</c>. Scoped, so an answer is never shared between users. Asked once per circuit, unless the
/// backend could not tell. An optional feature must not hurt the shell, so any failure just means "no tab".
/// </summary>
public sealed partial class KnowledgeAccess(
    HttpAssistantClient client,
    IOptions<AssistantOptions> options,
    ILogger<KnowledgeAccess> logger)
{
    private AssistantCapabilities? _capabilities;

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (!options.Value.IsEnabled)
            return false;
        try
        {
            _capabilities ??= await client.GetCapabilitiesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckFailed(ex);
            return false;
        }
        return _capabilities?.Features.Contains(AssistantProtocol.KnowledgeFeature) == true;
    }

    [LoggerMessage(EventId = 13083, Level = LogLevel.Warning, Message = "Could not check the assistant's knowledge capability")]
    private partial void LogCheckFailed(Exception ex);
}
