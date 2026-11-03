using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleans.Application.Bpmn;
using Fleans.Application.CustomTasks;
using Fleans.Assistant.Contracts;
using Fleans.ServiceDefaults.DTOs;

namespace Fleans.Web.Services;

/// <summary>
/// One editor tab's chat with the assistant (#1015). Kept in circuit memory on the tab; a page
/// reload loses it, while the diagram itself survives in localStorage.
/// </summary>
public sealed class AssistantConversation
{
    /// <param name="ProposalXml">The assistant's proposal, kept so the latest one can be sent back.</param>
    /// <param name="ProposalValid">The proposal passed <see cref="IBpmnConverter"/>; only these are offered to the user.</param>
    /// <param name="Automatic">A <c>fix:</c> request the editor sent on its own.</param>
    public sealed record Entry(string Role, string Text, string? ProposalXml = null, bool ProposalValid = false, bool Automatic = false);

    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Why the last turn ended without an answer; cleared when the next one starts.</summary>
    public AssistantError? LastError { get; set; }

    public void Add(Entry entry) => _entries.Add(entry);

    /// <summary>A copy for a tab opened from one of this conversation's proposals.</summary>
    public AssistantConversation Clone()
    {
        var copy = new AssistantConversation();
        copy._entries.AddRange(_entries);
        return copy;
    }
}

/// <summary>
/// Runs one chat turn of the editor's assistant panel (#1015): sends the conversation, the active
/// diagram, the plugin catalog and the engine version through <see cref="HttpAssistantClient"/>, then
/// checks the proposal with the engine's <see cref="IBpmnConverter"/>. A proposal that does not
/// convert is sent back as <c>fix: &lt;conversion error&gt;</c> up to <see cref="MaxAutomaticFixes"/>
/// times, so only XML the engine accepts is ever offered — whatever the backend.
/// </summary>
public sealed partial class EditorAssistant(
    HttpAssistantClient client,
    IBpmnConverter converter,
    IGrainFactory grainFactory,
    ILogger<EditorAssistant> logger)
{
    public const int MaxAutomaticFixes = 2;

    /// <summary>Stands in for every proposal but the latest, which keeps the request bounded.</summary>
    public const string ProposalPlaceholder = "[BPMN proposal omitted]";

    public static string EngineVersion { get; } =
        typeof(EditorAssistant).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "unknown";

    /// <summary>
    /// Adds <paramref name="userText"/> to <paramref name="conversation"/> and runs the turn to its end.
    /// </summary>
    /// <param name="currentXml">The active tab's diagram, <c>null</c> for an empty one.</param>
    /// <param name="onText">Each streamed text piece of the current attempt.</param>
    /// <param name="onAttemptFinished">An attempt's reply joined the conversation (its streamed text is now an entry).</param>
    /// <returns>A proposal that converts, or <c>null</c> (a text-only answer, or an error in <see cref="AssistantConversation.LastError"/>).</returns>
    public async Task<string?> RunTurnAsync(AssistantConversation conversation, string userText, string? currentXml,
        Func<string, Task> onText, Func<Task> onAttemptFinished, CancellationToken ct)
    {
        conversation.LastError = null;
        conversation.Add(new AssistantConversation.Entry(AssistantRoles.User, userText));
        var plugins = await LoadPluginsAsync();

        for (var attempt = 0; ; attempt++)
        {
            var reply = await client.SendAsync(
                new AssistantRequest(BuildMessages(conversation), currentXml, plugins, EngineVersion), onText, ct);

            if (reply.Error is not null || reply.ProposalXml is null)
            {
                if (reply.Text.Length > 0)
                    conversation.Add(new AssistantConversation.Entry(AssistantRoles.Assistant, reply.Text));
                conversation.LastError = reply.Error;
                await onAttemptFinished();
                return null;
            }

            var problem = await ValidateAsync(reply.ProposalXml);
            conversation.Add(new AssistantConversation.Entry(AssistantRoles.Assistant, reply.Text, reply.ProposalXml, problem is null));
            if (problem is null)
            {
                await onAttemptFinished();
                return reply.ProposalXml;
            }

            if (attempt == MaxAutomaticFixes)
            {
                LogStillInvalid(MaxAutomaticFixes);
                conversation.LastError = new AssistantError(AssistantErrorCodes.InvalidProposal, problem);
                await onAttemptFinished();
                return null;
            }

            LogAskingForFix(attempt + 1);
            conversation.Add(new AssistantConversation.Entry(AssistantRoles.User, "fix: " + problem, Automatic: true));
            await onAttemptFinished();
        }
    }

    /// <summary>The conversation as sent: only the latest proposal travels in full.</summary>
    internal static List<AssistantMessage> BuildMessages(AssistantConversation conversation)
    {
        var entries = conversation.Entries;
        var latestProposal = -1;
        for (var i = entries.Count - 1; i >= 0 && latestProposal < 0; i--)
            if (entries[i].ProposalXml is not null)
                latestProposal = i;

        var messages = new List<AssistantMessage>(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var text = e.ProposalXml is null
                ? e.Text
                : Join(e.Text, i == latestProposal ? "```xml\n" + e.ProposalXml + "\n```" : ProposalPlaceholder);
            messages.Add(new AssistantMessage(e.Role, text));
        }
        return messages;
    }

    private static string Join(string text, string tail) => text.Length == 0 ? tail : text + "\n\n" + tail;

    /// <returns>The conversion error, or <c>null</c> when the engine accepts the XML.</returns>
    private async Task<string?> ValidateAsync(string xml)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            await converter.ConvertFromXmlAsync(stream);
            return null;
        }
        catch (Exception ex)
        {
            LogProposalRejected(ex.GetType().Name);
            return ex.Message;
        }
    }

    private async Task<List<AssistantPlugin>> LoadPluginsAsync()
    {
        var entries = await grainFactory.GetGrain<ICustomTaskCatalogGrain>(0).GetAll();
        return entries
            .Select(e => new AssistantPlugin(
                e.TaskType,
                string.IsNullOrWhiteSpace(e.DisplayName) ? e.TaskType : e.DisplayName,
                e.ParameterSchema is null ? null : JsonSerializer.Serialize(e.ParameterSchema.ToDto(), AssistantProtocol.JsonOptions)))
            .ToList();
    }

    [LoggerMessage(EventId = 13075, Level = LogLevel.Information,
        Message = "The assistant's proposal failed BPMN conversion ({ExceptionType})")]
    private partial void LogProposalRejected(string exceptionType);

    [LoggerMessage(EventId = 13076, Level = LogLevel.Information,
        Message = "Asking the assistant to fix its proposal (automatic request {Attempt})")]
    private partial void LogAskingForFix(int attempt);

    [LoggerMessage(EventId = 13077, Level = LogLevel.Warning,
        Message = "The assistant's proposal still failed BPMN conversion after {Fixes} automatic fix requests")]
    private partial void LogStillInvalid(int fixes);
}
