using System.Text.Json;

namespace Fleans.Assistant.Contracts;

/// <summary>
/// One run of the assistant as the editor sees it. On the wire it is an AG-UI <c>RunAgentInput</c>
/// (<see cref="AssistantAgUi.ToRunAgentInput"/>) posted to <see cref="AssistantProtocol.AgentPath"/>, answered with an
/// AG-UI event stream. The editor is stateless towards the backend: every run carries the whole conversation, the
/// active tab's diagram, the plugin catalog and the engine version, so a backend needs no storage.
/// </summary>
/// <param name="Messages">The conversation, oldest first; the last one is the user's new request.
/// Only the latest assistant message carries its proposal (as a fenced <c>xml</c> block); earlier
/// proposals are replaced by a short placeholder.</param>
/// <param name="CurrentXml">The active tab's BPMN XML; <c>null</c> when it is an empty diagram.</param>
/// <param name="Plugins">The custom-task types registered on the cluster, which a proposal may use.</param>
/// <param name="EngineVersion">The Fleans version, so a backend can match the BPMN subset it targets.</param>
/// <param name="ConversationId">A stored chat (#1017) from <c>POST /assistant/conversations</c>, sent as the run's
/// <c>threadId</c>. Set: the backend holds the history and <paramref name="Messages"/> carries only the user's new turn.
/// <c>null</c> (the default): the stateless run above, with a fresh <c>threadId</c>.</param>
/// <param name="Approvals">The user's answers to <see cref="AssistantApprovalRequest"/>s (#1017), sent as the run's
/// <c>resume</c> entries. A click on Run or Cancel is an ordinary run with these set and an empty user text.</param>
public sealed record AssistantRequest(
    List<AssistantMessage> Messages,
    string? CurrentXml,
    List<AssistantPlugin> Plugins,
    string EngineVersion,
    string? ConversationId = null,
    List<AssistantApprovalDecision>? Approvals = null);

/// <summary>One turn of the conversation.</summary>
/// <param name="Role"><see cref="AssistantRoles.User"/> or <see cref="AssistantRoles.Assistant"/>.</param>
public sealed record AssistantMessage(string Role, string Text);

/// <summary>A registered custom-task type (<c>&lt;bpmn:serviceTask&gt;</c> with a <c>fleans:taskDefinition type</c>).</summary>
/// <param name="ParameterSchemaJson">The plugin's input parameters as the JSON of <c>GET /custom-tasks</c>'
/// <c>parameterSchema</c>; <c>null</c> when the plugin declares none.</param>
public sealed record AssistantPlugin(string TaskType, string DisplayName, string? ParameterSchemaJson);

/// <summary>Why a run or call failed: an AG-UI <c>RUN_ERROR</c> (<c>code</c>, <c>message</c>), the JSON body of a
/// non-success HTTP answer, or an error the editor's client sets itself.</summary>
/// <param name="Code">Machine-readable, e.g. <c>rate_limited</c>; see <see cref="AssistantErrorCodes"/>.</param>
/// <param name="Message">Shown to the user as is.</param>
public sealed record AssistantError(string Code, string Message);

/// <summary>The wire names of the contract.</summary>
public static class AssistantProtocol
{
    /// <summary>The AG-UI agent endpoint (#1054), appended to the configured endpoint (<c>Fleans:Assistant:Endpoint</c>):
    /// <c>POST</c> a <c>RunAgentInput</c>, answered with an AG-UI <c>text/event-stream</c>. See <see cref="AssistantAgUi"/>.</summary>
    public const string AgentPath = "assistant/agent";

    /// <summary><c>GET</c> → <see cref="AssistantCapabilities"/>. A 404 means the backend only serves the agent run.</summary>
    public const string CapabilitiesPath = "assistant/capabilities";

    /// <summary>Stored chats (#1017): <c>POST</c> creates one (201 <see cref="AssistantConversation"/>),
    /// <c>GET</c> lists them (<see cref="AssistantConversationPage"/>, <c>?cursor=</c>), <c>GET {id}</c>
    /// returns <see cref="AssistantConversationHistory"/> and <c>DELETE {id}</c> removes one (204).
    /// Every call needs the same bearer token as the run; a user only sees their own chats.</summary>
    public const string ConversationsPath = "assistant/conversations";

    /// <summary>Under <see cref="ConversationsPath"/>/{id}: <c>GET</c> is an AG-UI <c>text/event-stream</c> of what happens in the
    /// chat outside a run (tool calls, interrupts, <see cref="AssistantAgUi.JobEventName"/> custom events).
    /// Each event has an SSE <c>id:</c>; the client resumes with <c>Last-Event-ID</c>, and the backend may end the stream at any time.</summary>
    public const string ConversationEventsSegment = "events";

    /// <summary><c>POST {path}/{jobId}/cancel</c> asks the backend to stop a job (202); the outcome arrives as a job event.</summary>
    public const string JobsPath = "assistant/jobs";

    /// <summary>The <see cref="AssistantCapabilities.Features"/> entry of a backend that stores chats.</summary>
    public const string PersistenceFeature = "persistence";

    /// <summary>The <see cref="AssistantCapabilities.Features"/> entry of a backend that serves the knowledge index (#1025).</summary>
    public const string KnowledgeFeature = "knowledge";

    /// <summary>The read-only knowledge index (#1025): <c>GET sources</c>, <c>search</c>, <c>files</c>, <c>chunks</c>, <c>adrs</c>
    /// under this path, each limited to what the caller may read. A repository, path or project the caller may not read
    /// is a 404.</summary>
    public const string KnowledgePath = "api/ai/knowledge";

    /// <summary>
    /// JSON for the Fleans-specific bodies (capabilities, conversations, knowledge, errors, job payloads): camelCase
    /// property names (<see cref="JsonSerializerDefaults.Web"/>), e.g. <c>{"bpmnXml":"…"}</c>. AG-UI types use
    /// <c>AGUIJsonSerializerContext</c>.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public static class AssistantRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>
/// Error codes the editor tells apart. A backend may send any code in an <c>error</c> event;
/// the editor shows its message. The HTTP-level ones are set by the editor's client.
/// </summary>
public static class AssistantErrorCodes
{
    /// <summary>HTTP 401 — the forwarded access token was missing, expired or rejected.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>HTTP 429 — the backend (or its model provider) is rate limiting.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>HTTP 5xx or the backend could not be reached.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>Any other non-success status, or a stream the client could not read.</summary>
    public const string BadResponse = "bad_response";

    /// <summary>The response did not finish within the editor's timeout.</summary>
    public const string Timeout = "timeout";

    /// <summary>The proposal still failed the engine's BPMN conversion after the editor's automatic fix requests.</summary>
    public const string InvalidProposal = "invalid_proposal";

    /// <summary>HTTP 404 — the stored chat does not exist for this user (or was deleted).</summary>
    public const string ConversationNotFound = "conversation_not_found";

    /// <summary>The user may not do this action on this resource.</summary>
    public const string ForbiddenAction = "forbidden_action";

    /// <summary>The approval came after its <see cref="AssistantApprovalRequest.ExpiresAt"/>; nothing ran.</summary>
    public const string ApprovalExpired = "approval_expired";

    /// <summary>The approval names an unknown action, or one that was already decided; nothing ran.</summary>
    public const string ApprovalInvalid = "approval_invalid";

    /// <summary>The request needs a feature this backend has not enabled.</summary>
    public const string IntegrationUnavailable = "integration_unavailable";

    /// <summary>A job could not be run or reported.</summary>
    public const string JobFailed = "job_failed";

    /// <summary>The user used up the backend's token budget.</summary>
    public const string BudgetExceeded = "budget_exceeded";
}

/// <summary>The values of <see cref="AssistantActivity.Status"/>.</summary>
public static class AssistantActivityStatus
{
    public const string Started = "started";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>The values of <see cref="AssistantJobUpdate.Status"/>.</summary>
public static class AssistantJobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>A job in this status has finished and will not change again.</summary>
    public static bool IsFinal(string status) => status is Succeeded or Failed or Cancelled;
}

/// <summary><c>GET /assistant/capabilities</c>: what the backend can do beyond the chat call.</summary>
/// <param name="ContractVersion">The highest contract version the backend speaks.</param>
/// <param name="Features">Enabled features, e.g. <c>bpmn</c> and <see cref="AssistantProtocol.PersistenceFeature"/>.
/// Clients ignore names they do not know.</param>
public sealed record AssistantCapabilities(int ContractVersion, List<string> Features);

/// <summary>A stored chat: the answer to <c>POST /assistant/conversations</c>.</summary>
/// <param name="Title">Set by the backend (e.g. from the first message); <c>null</c> until then.</param>
public sealed record AssistantConversation(string ConversationId, string? Title);

/// <summary>The body of <c>PATCH /assistant/conversations/{id}</c>, which renames a chat.</summary>
public sealed record AssistantConversationUpdate(string? Title);

/// <summary>The user's answer to one <see cref="AssistantApprovalRequest"/>: an AG-UI <c>resume</c> entry
/// (<see cref="AssistantAgUi.ToResume"/>).</summary>
public sealed record AssistantApprovalDecision(string ActionId, bool Approved, string? Comment = null);

/// <summary>An agent started or finished using a tool: an AG-UI <c>TOOL_CALL_START</c> / <c>TOOL_CALL_RESULT</c> of a
/// backend tool (<see cref="AssistantAgUi"/>). Events with the same id update one line.</summary>
/// <param name="Status">See <see cref="AssistantActivityStatus"/>.</param>
public sealed record AssistantActivity(string Id, string Agent, string Tool, string Status, string Summary);

/// <summary>An action that changes something runs only after the user approves it: an AG-UI interrupt of a
/// <c>RUN_FINISHED</c> with an <c>interrupt</c> outcome (<see cref="AssistantAgUi.ToInterrupt"/>).</summary>
/// <param name="Kind">The backend's name for the action, e.g. <c>deploy.apply</c>.</param>
/// <param name="DetailsMarkdown">What will change. Shown as Markdown, with links limited to http(s) and mailto.</param>
public sealed record AssistantApprovalRequest(string ActionId, string Kind, string Summary, string DetailsMarkdown, DateTimeOffset ExpiresAt);

/// <summary>A long-running job changed state: an AG-UI <c>CUSTOM</c> event named <see cref="AssistantAgUi.JobEventName"/>
/// carrying this record. Events with the same id update one card.</summary>
/// <param name="Status">See <see cref="AssistantJobStatus"/>.</param>
/// <param name="Url">Where to read more; only http(s) links are shown.</param>
public sealed record AssistantJobUpdate(string JobId, string Kind, string Status, string? Summary, string? Url);

/// <summary>A chat in <c>GET /assistant/conversations</c>.</summary>
public sealed record AssistantConversationInfo(string ConversationId, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The caller's chats, most recently updated first; pass <see cref="NextCursor"/> as <c>?cursor=</c> for more.</summary>
public sealed record AssistantConversationPage(List<AssistantConversationInfo> Items, string? NextCursor);

/// <summary>A stored turn of <c>GET /assistant/conversations/{id}</c>.</summary>
/// <param name="BpmnXml">The diagram the assistant proposed in this turn, if any.</param>
public sealed record AssistantStoredMessage(long Seq, string Role, string Text, string? BpmnXml, DateTimeOffset CreatedAt);

/// <summary><c>GET /assistant/conversations/{id}</c>: the chat and its turns, oldest first.</summary>
public sealed record AssistantConversationHistory(AssistantConversationInfo Conversation, List<AssistantStoredMessage> Messages);

/// <summary>What the knowledge index cuts a repository into; the values of <c>kind</c>.</summary>
public static class KnowledgeKinds
{
    public const string Doc = "doc";
    public const string Adr = "adr";
    public const string Code = "code";
    public const string Bpmn = "bpmn";
}

/// <summary>An indexed version of a repository: a row of <c>GET sources</c>.</summary>
/// <param name="Kinds">Chunk counts per <see cref="KnowledgeKinds"/> value; a kind without chunks is absent.</param>
/// <param name="WebUrl">The repository's page in the git provider, if the backend gives one. Files link to
/// <c>{WebUrl}/blob/{commit}/{path}#L{line}</c>, a form GitHub and GitLab both serve.</param>
public sealed record KnowledgeSource(
    string Project,
    string Repo,
    string Branch,
    string CommitSha,
    string EngineVersion,
    DateTimeOffset IndexedAt,
    int Chunks,
    bool HasEmbeddings,
    Dictionary<string, int> Kinds,
    string? WebUrl = null);

/// <summary>A chunk with the version it comes from: a row of <c>GET search</c>, <c>chunks</c> and <c>adrs</c>.</summary>
/// <param name="Line">The chunk's first line in its file (1-based).</param>
/// <param name="Symbol">The C# symbol or BPMN process id, if the chunk has one.</param>
/// <param name="Title">A heading path, a signature or a process name.</param>
/// <param name="Score">The search rank; meaningless outside a search.</param>
public sealed record KnowledgeHit(
    string Project,
    string Repo,
    string Branch,
    string CommitSha,
    string Path,
    int Line,
    string Kind,
    string? Symbol,
    string Title,
    string Content,
    double Score);

/// <summary>A file of the index: a row of <c>GET files</c>.</summary>
public sealed record KnowledgeFile(
    string Project,
    string Repo,
    string Branch,
    string CommitSha,
    string Path,
    int Line,
    string Kind,
    string Title,
    int Chunks);

/// <summary>The answer of <c>GET files</c>; pass <paramref name="NextCursor"/> as <c>?cursor=</c> for the next page.</summary>
public sealed record KnowledgeFilePage(List<KnowledgeFile> Items, string? NextCursor);

/// <summary>The answer of <c>GET chunks</c>.</summary>
/// <param name="Truncated">The file has more chunks than one answer returns.</param>
public sealed record KnowledgeChunks(List<KnowledgeHit> Items, bool Truncated);
