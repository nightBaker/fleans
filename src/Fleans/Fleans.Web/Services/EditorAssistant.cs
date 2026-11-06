using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleans.Application.Bpmn;
using Fleans.Application.CustomTasks;
using Fleans.Assistant.Contracts;
using Fleans.ServiceDefaults.DTOs;

namespace Fleans.Web.Services;

/// <summary>
/// One editor tab's chat with the assistant (#1015). The entries live in circuit memory on the tab. With a
/// backend that stores chats (#1017) the tab also keeps <see cref="ConversationId"/>, which survives a page
/// reload with the tab, and the entries are read back from the backend; otherwise a reload loses the chat,
/// while the diagram itself survives in localStorage.
/// </summary>
public sealed class AssistantChat
{
    /// <param name="ProposalXml">The assistant's proposal, kept so the latest one can be sent back.</param>
    /// <param name="ProposalValid">The proposal passed <see cref="IBpmnConverter"/>; only these are offered to the user.</param>
    /// <param name="Automatic">A <c>fix:</c> request the editor sent on its own.</param>
    public sealed record Entry(string Role, string Text, string? ProposalXml = null, bool ProposalValid = false, bool Automatic = false)
    {
        /// <summary>Matches this message to its revision in the tab's <see cref="AssistantChangeHistory"/>, also after a reload.</summary>
        public string? ProposalHash { get; } = ProposalXml is null ? null : AssistantChangeHistory.HashOf(ProposalXml);
    }

    private readonly List<Entry> _entries = new();
    private readonly List<AssistantActivity> _activities = new();
    private readonly List<AssistantApprovalRequest> _approvals = new();
    private readonly List<AssistantJobUpdate> _jobs = new();

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>What the agents did in the latest turn, one line per id.</summary>
    public IReadOnlyList<AssistantActivity> Activities => _activities;

    /// <summary>Actions waiting for the user's Run or Cancel, one card per action.</summary>
    public IReadOnlyList<AssistantApprovalRequest> Approvals => _approvals;

    /// <summary>This chat's long-running jobs, one card per job.</summary>
    public IReadOnlyList<AssistantJobUpdate> Jobs => _jobs;

    /// <summary>How far the chat's event feed has been read; <c>null</c> reads it from the start.</summary>
    public string? LastEventId { get; set; }

    /// <summary>Why the last turn ended without an answer; cleared when the next one starts.</summary>
    public AssistantError? LastError { get; set; }

    /// <summary>The backend's stored chat (#1017); <c>null</c> until the first message, or when the backend keeps no chats.</summary>
    public string? ConversationId { get; set; }

    public void Add(Entry entry) => _entries.Add(entry);

    /// <summary>Records one report of the backend. A report without its id is ignored; a later report of the same id replaces
    /// the earlier one. The chat stream and the event feed both report jobs, so a finished job stays finished.</summary>
    public void Apply(AssistantProgress progress)
    {
        if (progress.Activity is { Id.Length: > 0 } activity)
            Upsert(_activities, activity, a => a.Id == activity.Id);
        if (progress.Approval is { ActionId.Length: > 0 } approval)
            Upsert(_approvals, approval, a => a.ActionId == approval.ActionId);
        if (progress.Job is { JobId.Length: > 0 } job
            && !_jobs.Exists(j => j.JobId == job.JobId && AssistantJobStatus.IsFinal(j.Status)))
            Upsert(_jobs, job, j => j.JobId == job.JobId);
    }

    private static void Upsert<T>(List<T> items, T item, Predicate<T> same)
    {
        var index = items.FindIndex(same);
        if (index >= 0)
            items[index] = item;
        else
            items.Add(item);
    }

    public void RemoveApproval(string actionId) => _approvals.RemoveAll(a => a.ActionId == actionId);

    /// <summary>A new turn starts: the activity lines describe the latest one only.</summary>
    public void ClearActivities() => _activities.Clear();

    /// <summary>The backend no longer has this chat: the next message starts a new one. Nothing it reported can be acted on.</summary>
    public void ChatGone()
    {
        ConversationId = null;
        LastEventId = null;
        _approvals.Clear();
        _jobs.Clear();
    }

    /// <summary>What the user asked for when <paramref name="reply"/> came back; automatic <c>fix:</c> requests don't count.</summary>
    public string? RequestFor(Entry reply)
    {
        for (var i = _entries.FindLastIndex(e => ReferenceEquals(e, reply)) - 1; i >= 0; i--)
            if (_entries[i] is { Role: AssistantRoles.User, Automatic: false } request)
                return request.Text;
        return null;
    }

    /// <summary>Empties the chat and points it at <paramref name="conversationId"/> (<c>null</c>: a new chat).</summary>
    public void Reset(string? conversationId)
    {
        _entries.Clear();
        _activities.Clear();
        _approvals.Clear();
        _jobs.Clear();
        LastError = null;
        LastEventId = null;
        ConversationId = conversationId;
    }

    /// <summary>A copy for a tab opened from one of this conversation's proposals; it continues the same stored chat, and
    /// reads the chat's approvals and jobs from its event feed.</summary>
    public AssistantChat Clone()
    {
        var copy = new AssistantChat { ConversationId = ConversationId };
        copy._entries.AddRange(_entries);
        return copy;
    }
}

/// <summary>
/// Runs one chat turn of the editor's assistant panel (#1015): sends the conversation, the active
/// diagram, the plugin catalog and the engine version through <see cref="HttpAssistantClient"/>, then
/// checks the proposal with the engine's <see cref="IBpmnConverter"/>. A proposal that does not
/// convert is sent back as <c>fix: &lt;conversion error&gt;</c> up to <see cref="MaxAutomaticFixes"/>
/// times, so only XML the engine accepts is ever offered — whatever the backend. When the backend stores
/// chats (#1017, <see cref="StoresChatsAsync"/>), each request carries the chat's id and only the new turn,
/// <see cref="OpenChatAsync"/> reads a stored chat back into a tab, <see cref="DecideAsync"/> answers an
/// approval and <see cref="WatchChatAsync"/> follows what happens in the chat between requests.
/// </summary>
public sealed partial class EditorAssistant(
    HttpAssistantClient client,
    IBpmnConverter converter,
    IGrainFactory grainFactory,
    ILogger<EditorAssistant> logger)
{
    public const int MaxAutomaticFixes = 2;

    /// <summary>Starts the automatic request that asks the backend to fix a proposal the engine rejected.</summary>
    public const string FixPrefix = "fix: ";

    // Scoped per circuit: asked once, unless the backend could not tell.
    private AssistantCapabilities? _capabilities;

    // Actions answered in this circuit: the event feed repeats them when a chat is opened again. After a page
    // reload a decided action can come back; the backend refuses a second answer and the card goes away.
    private readonly HashSet<string> _decidedApprovals = new();

    /// <summary>The pause before the event feed connects again.</summary>
    internal TimeSpan EventsReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Stands in for every proposal but the latest, which keeps the request bounded.</summary>
    public const string ProposalPlaceholder = "[BPMN proposal omitted]";

    public static string EngineVersion { get; } =
        typeof(EditorAssistant).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "unknown";

    /// <summary>
    /// Adds <paramref name="userText"/> to <paramref name="conversation"/> and runs the turn to its end.
    /// </summary>
    /// <param name="currentXml">The active tab's diagram, <c>null</c> for an empty one.</param>
    /// <param name="processKey">The active tab's process: the assistant is only told about plugins it may use.</param>
    /// <param name="onText">Each streamed text piece of the current attempt.</param>
    /// <param name="onAttemptFinished">An attempt's reply joined the conversation (its streamed text is now an entry).</param>
    /// <param name="onProgress">An activity line, approval or job in <paramref name="conversation"/> changed.</param>
    /// <returns>A proposal that converts, or <c>null</c> (a text-only answer, or an error in <see cref="AssistantChat.LastError"/>).</returns>
    public async Task<string?> RunTurnAsync(AssistantChat conversation, string userText, string? currentXml,
        Func<string, Task> onText, Func<Task> onAttemptFinished, CancellationToken ct, string? processKey = null, Func<Task>? onProgress = null)
    {
        conversation.LastError = null;
        conversation.ClearActivities();
        conversation.Add(new AssistantChat.Entry(AssistantRoles.User, userText));
        var stored = await StoresChatsAsync(ct);
        if (stored && conversation.ConversationId is null)
        {
            var (created, error) = await client.CreateConversationAsync(ct);
            if (created is null)
            {
                conversation.LastError = error;
                await onAttemptFinished();
                return null;
            }
            conversation.ConversationId = created.ConversationId;
            LogChatCreated();
        }
        var plugins = await LoadPluginsAsync(processKey);

        for (var attempt = 0; ; attempt++)
        {
            var request = stored
                ? new AssistantRequest(NewTurn(conversation), currentXml, plugins, EngineVersion, conversation.ConversationId)
                : new AssistantRequest(BuildMessages(conversation), currentXml, plugins, EngineVersion);
            var reply = await client.SendAsync(request, onText, ct, TrackProgress(conversation, onProgress));

            if (reply.Error is not null || reply.ProposalXml is null)
            {
                if (reply.Text.Length > 0)
                    conversation.Add(new AssistantChat.Entry(AssistantRoles.Assistant, reply.Text));
                if (reply.Error?.Code == AssistantErrorCodes.ConversationNotFound)
                    conversation.ChatGone(); // deleted elsewhere: the next message starts a new chat
                conversation.LastError = reply.Error;
                await onAttemptFinished();
                return null;
            }

            var problem = await ValidateAsync(reply.ProposalXml);
            conversation.Add(new AssistantChat.Entry(AssistantRoles.Assistant, reply.Text, reply.ProposalXml, problem is null));
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
            conversation.Add(new AssistantChat.Entry(AssistantRoles.User, FixPrefix + problem, Automatic: true));
            await onAttemptFinished();
        }
    }

    /// <summary>
    /// Answers an approval the way the contract says: an ordinary chat request with the decision and an empty user
    /// text, plus the diagram and catalog every turn carries. The backend re-checks everything and runs the action only
    /// once, so a card stays after a failure that says nothing about the decision, and trying again is safe.
    /// </summary>
    /// <param name="approved"><c>true</c> for Run, <c>false</c> for Cancel.</param>
    /// <param name="onText">Each streamed text piece of the answer.</param>
    /// <param name="onFinished">The answer joined the chat.</param>
    public async Task DecideAsync(AssistantChat chat, AssistantApprovalRequest approval, bool approved, string? currentXml,
        Func<string, Task> onText, Func<Task> onFinished, CancellationToken ct, string? processKey = null, Func<Task>? onProgress = null)
    {
        chat.LastError = null;
        chat.ClearActivities();
        if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            chat.RemoveApproval(approval.ActionId);
            chat.LastError = new AssistantError(AssistantErrorCodes.ApprovalExpired, "");
            LogApprovalAnswered(approval.Kind, approved, AssistantErrorCodes.ApprovalExpired);
            await onFinished();
            return;
        }

        var request = new AssistantRequest([new AssistantMessage(AssistantRoles.User, "")], currentXml,
            await LoadPluginsAsync(processKey), EngineVersion, chat.ConversationId,
            [new AssistantApprovalDecision(approval.ActionId, approved)]);
        var reply = await client.SendAsync(request, onText, ct, TrackProgress(chat, onProgress));

        if (reply.Text.Length > 0)
            chat.Add(new AssistantChat.Entry(AssistantRoles.Assistant, reply.Text));
        chat.LastError = reply.Error;
        if (reply.Error is null or { Code: AssistantErrorCodes.ApprovalInvalid or AssistantErrorCodes.ApprovalExpired or AssistantErrorCodes.ForbiddenAction })
        {
            _decidedApprovals.Add(approval.ActionId);
            chat.RemoveApproval(approval.ActionId);
        }
        if (reply.Error?.Code == AssistantErrorCodes.ConversationNotFound)
            chat.ChatGone();
        LogApprovalAnswered(approval.Kind, approved, reply.Error?.Code);
        await onFinished();
    }

    /// <summary>
    /// Follows the stored chat's event feed until <paramref name="ct"/> is cancelled, the chat moves on, or there is no
    /// feed to follow. Reports go into <paramref name="chat"/>; a connection that ends is opened again from the last id.
    /// Failures stay here: the feed runs in the background, and the user's next message reports a real problem.
    /// </summary>
    /// <param name="onChanged">An activity line, approval or job in <paramref name="chat"/> changed.</param>
    public async Task WatchChatAsync(AssistantChat chat, string conversationId, Func<Task> onChanged, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && chat.ConversationId == conversationId)
            {
                var keepWatching = await client.ReadEventsAsync(conversationId, chat.LastEventId, async (eventId, progress) =>
                {
                    if (chat.ConversationId != conversationId)
                        return;
                    if (eventId is not null)
                        chat.LastEventId = eventId;
                    if (progress is not null)
                    {
                        Record(chat, progress);
                        await onChanged();
                    }
                }, ct);
                if (!keepWatching)
                {
                    LogEventFeedStopped();
                    return;
                }
                await Task.Delay(EventsReconnectDelay, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The panel closed, the tab switched or the chat changed.
        }
    }

    private Func<AssistantProgress, Task> TrackProgress(AssistantChat chat, Func<Task>? onProgress) => progress =>
    {
        Record(chat, progress);
        return onProgress?.Invoke() ?? Task.CompletedTask;
    };

    private void Record(AssistantChat chat, AssistantProgress progress)
    {
        // Nothing can answer an approval that has run out or was answered in this circuit.
        if (progress.Approval is { } approval && (approval.ExpiresAt <= DateTimeOffset.UtcNow || (approval.ActionId is { } id && _decidedApprovals.Contains(id))))
            return;
        chat.Apply(progress);
    }

    /// <summary><c>null</c> when the backend took the request; the outcome arrives as a <c>job</c> event.</summary>
    public async Task<AssistantError?> CancelJobAsync(string jobId, CancellationToken ct)
    {
        var error = await client.CancelJobAsync(jobId, ct);
        LogJobCancelRequested(error?.Code);
        return error;
    }

    /// <summary><c>null</c> on success.</summary>
    public async Task<AssistantError?> RenameChatAsync(string conversationId, string title, CancellationToken ct)
    {
        var error = await client.RenameConversationAsync(conversationId, title, ct);
        LogChatRenamed(error?.Code);
        return error;
    }

    /// <summary>Whether the backend keeps chats (<c>persistence</c> in its capabilities). Asked once per circuit;
    /// a backend that could not answer counts as one that does not, and is asked again next time.</summary>
    public async Task<bool> StoresChatsAsync(CancellationToken ct)
    {
        _capabilities ??= await client.GetCapabilitiesAsync(ct);
        return _capabilities?.Features.Contains(AssistantProtocol.PersistenceFeature) == true;
    }

    /// <summary>The user's stored chats, most recently updated first.</summary>
    /// <param name="cursor">The previous page's <see cref="AssistantConversationPage.NextCursor"/>; <c>null</c> for the first page.</param>
    public Task<(AssistantConversationPage? Page, AssistantError? Error)> ListChatsAsync(CancellationToken ct, string? cursor = null) =>
        client.ListConversationsAsync(ct, cursor);

    /// <summary>
    /// Replaces <paramref name="chat"/>'s entries with the stored chat <paramref name="conversationId"/>. Stored
    /// proposals go through the engine's BPMN check again, so only XML that converts can be applied.
    /// A chat that is gone leaves <paramref name="chat"/> empty and new, with the error in <see cref="AssistantChat.LastError"/>.
    /// </summary>
    public async Task OpenChatAsync(AssistantChat chat, string conversationId, CancellationToken ct)
    {
        var (history, error) = await client.GetConversationAsync(conversationId, ct);
        if (history is null)
        {
            chat.Reset(error?.Code == AssistantErrorCodes.ConversationNotFound ? null : conversationId);
            chat.LastError = error;
            return;
        }

        chat.Reset(conversationId);
        foreach (var m in history.Messages)
        {
            var valid = m.BpmnXml is not null && await ValidateAsync(m.BpmnXml) is null;
            var automatic = m.Role == AssistantRoles.User && m.Text.StartsWith(FixPrefix, StringComparison.Ordinal);
            chat.Add(new AssistantChat.Entry(m.Role, m.Text, m.BpmnXml, valid, automatic));
        }
        LogChatOpened(history.Messages.Count);
    }

    /// <summary>Deletes the stored chat and empties <paramref name="chat"/>; <c>null</c> on success.</summary>
    public async Task<AssistantError?> DeleteChatAsync(AssistantChat chat, CancellationToken ct)
    {
        if (chat.ConversationId is { } id)
        {
            var error = await client.DeleteConversationAsync(id, ct);
            if (error is not null && error.Code != AssistantErrorCodes.ConversationNotFound)
                return error;
            LogChatDeleted();
        }
        chat.Reset(null);
        return null;
    }

    /// <summary>A stored chat's request: the backend holds the history, so only the newest turn travels.</summary>
    internal static List<AssistantMessage> NewTurn(AssistantChat conversation)
    {
        var last = conversation.Entries[^1];
        return [new AssistantMessage(last.Role, last.Text)];
    }

    /// <summary>The conversation as sent: only the latest proposal travels in full.</summary>
    internal static List<AssistantMessage> BuildMessages(AssistantChat conversation)
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

    private async Task<List<AssistantPlugin>> LoadPluginsAsync(string? processKey)
    {
        var entries = await grainFactory.GetGrain<ICustomTaskCatalogGrain>(0).GetAll();
        return entries
            .Where(e => e.IsOfferedTo(processKey))
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

    [LoggerMessage(EventId = 13080, Level = LogLevel.Information, Message = "Started a stored assistant chat")]
    private partial void LogChatCreated();

    [LoggerMessage(EventId = 13081, Level = LogLevel.Information, Message = "Opened a stored assistant chat with {MessageCount} messages")]
    private partial void LogChatOpened(int messageCount);

    [LoggerMessage(EventId = 13082, Level = LogLevel.Information, Message = "Deleted a stored assistant chat")]
    private partial void LogChatDeleted();

    [LoggerMessage(EventId = 13089, Level = LogLevel.Information,
        Message = "Answered an assistant approval of kind {Kind}: approved {Approved}, error {ErrorCode}")]
    private partial void LogApprovalAnswered(string kind, bool approved, string? errorCode);

    [LoggerMessage(EventId = 13090, Level = LogLevel.Information, Message = "Asked the assistant to cancel a job: error {ErrorCode}")]
    private partial void LogJobCancelRequested(string? errorCode);

    [LoggerMessage(EventId = 13091, Level = LogLevel.Information, Message = "Renamed a stored assistant chat: error {ErrorCode}")]
    private partial void LogChatRenamed(string? errorCode);

    [LoggerMessage(EventId = 13092, Level = LogLevel.Debug, Message = "Stopped following an assistant chat's events: the backend has no feed for it or rejected the token")]
    private partial void LogEventFeedStopped();
}
