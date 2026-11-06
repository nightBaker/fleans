using System.Text.Json;

namespace Fleans.Assistant.Contracts;

/// <summary>
/// The body of <c>POST /assistant/chat</c>. The editor is stateless towards the backend: every call
/// carries the whole conversation, the active tab's diagram, the plugin catalog and the engine
/// version, so a backend needs no storage. The response is a <c>text/event-stream</c> of
/// <see cref="AssistantTextDelta"/>, <see cref="AssistantProposal"/> and <see cref="AssistantError"/>.
/// </summary>
/// <param name="Messages">The conversation, oldest first; the last one is the user's new request.
/// Only the latest assistant message carries its proposal (as a fenced <c>xml</c> block); earlier
/// proposals are replaced by a short placeholder.</param>
/// <param name="CurrentXml">The active tab's BPMN XML; <c>null</c> when it is an empty diagram.</param>
/// <param name="Plugins">The custom-task types registered on the cluster, which a proposal may use.</param>
/// <param name="EngineVersion">The Fleans version, so a backend can match the BPMN subset it targets.</param>
/// <param name="ConversationId">A stored chat (#1017) from <c>POST /assistant/conversations</c>. Set: the
/// backend holds the history and <paramref name="Messages"/> carries only the user's new turn.
/// <c>null</c> (the default): the stateless call above.</param>
public sealed record AssistantRequest(
    List<AssistantMessage> Messages,
    string? CurrentXml,
    List<AssistantPlugin> Plugins,
    string EngineVersion,
    string? ConversationId = null);

/// <summary>One turn of the conversation.</summary>
/// <param name="Role"><see cref="AssistantRoles.User"/> or <see cref="AssistantRoles.Assistant"/>.</param>
public sealed record AssistantMessage(string Role, string Text);

/// <summary>A registered custom-task type (<c>&lt;bpmn:serviceTask&gt;</c> with a <c>fleans:taskDefinition type</c>).</summary>
/// <param name="ParameterSchemaJson">The plugin's input parameters as the JSON of <c>GET /custom-tasks</c>'
/// <c>parameterSchema</c>; <c>null</c> when the plugin declares none.</param>
public sealed record AssistantPlugin(string TaskType, string DisplayName, string? ParameterSchemaJson);

/// <summary><c>event: text</c> — the next piece of the assistant's reply, appended as it arrives.</summary>
public sealed record AssistantTextDelta(string Text);

/// <summary>
/// <c>event: proposal</c> — a complete BPMN 2.0 diagram (at most one per response). Diagram
/// interchange (<c>bpmndi</c>) is optional: the editor lays out a process without it.
/// </summary>
public sealed record AssistantProposal(string BpmnXml);

/// <summary><c>event: error</c> — the backend could not answer; it ends the response.</summary>
/// <param name="Code">Machine-readable, e.g. <c>rate_limited</c>; see <see cref="AssistantErrorCodes"/>.</param>
/// <param name="Message">Shown to the user as is.</param>
public sealed record AssistantError(string Code, string Message);

/// <summary>The wire names of the contract.</summary>
public static class AssistantProtocol
{
    /// <summary>The path appended to the configured endpoint (<c>Fleans:Assistant:Endpoint</c>).</summary>
    public const string ChatPath = "assistant/chat";

    /// <summary><c>GET</c> → <see cref="AssistantCapabilities"/>. A 404 means the backend only serves the chat call.</summary>
    public const string CapabilitiesPath = "assistant/capabilities";

    /// <summary>Stored chats (#1017): <c>POST</c> creates one (201 <see cref="AssistantConversation"/>),
    /// <c>GET</c> lists them (<see cref="AssistantConversationPage"/>, <c>?cursor=</c>), <c>GET {id}</c>
    /// returns <see cref="AssistantConversationHistory"/> and <c>DELETE {id}</c> removes one (204).
    /// Every call needs the same bearer token as the chat; a user only sees their own chats.</summary>
    public const string ConversationsPath = "assistant/conversations";

    public const string TextEvent = "text";
    public const string ProposalEvent = "proposal";
    public const string ErrorEvent = "error";

    /// <summary>First event of a response to a request with a <c>ConversationId</c>: <see cref="AssistantConversation"/>.</summary>
    public const string ConversationEvent = "conversation";

    /// <summary>The <see cref="AssistantCapabilities.Features"/> entry of a backend that stores chats.</summary>
    public const string PersistenceFeature = "persistence";

    /// <summary>
    /// JSON for the request body and every event's <c>data</c>: camelCase property names
    /// (<see cref="JsonSerializerDefaults.Web"/>), e.g. <c>{"bpmnXml":"…"}</c>.
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
}

/// <summary><c>GET /assistant/capabilities</c>: what the backend can do beyond the chat call.</summary>
/// <param name="ContractVersion">The highest contract version the backend speaks.</param>
/// <param name="Features">Enabled features, e.g. <c>bpmn</c> and <see cref="AssistantProtocol.PersistenceFeature"/>.
/// Clients ignore names they do not know.</param>
public sealed record AssistantCapabilities(int ContractVersion, List<string> Features);

/// <summary>A stored chat: the answer to <c>POST /assistant/conversations</c> and the <c>conversation</c> event.</summary>
/// <param name="Title">Set by the backend (e.g. from the first message); <c>null</c> until then.</param>
public sealed record AssistantConversation(string ConversationId, string? Title);

/// <summary>A chat in <c>GET /assistant/conversations</c>.</summary>
public sealed record AssistantConversationInfo(string ConversationId, string? Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The caller's chats, most recently updated first; pass <see cref="NextCursor"/> as <c>?cursor=</c> for more.</summary>
public sealed record AssistantConversationPage(List<AssistantConversationInfo> Items, string? NextCursor);

/// <summary>A stored turn of <c>GET /assistant/conversations/{id}</c>.</summary>
/// <param name="BpmnXml">The diagram the assistant proposed in this turn, if any.</param>
public sealed record AssistantStoredMessage(long Seq, string Role, string Text, string? BpmnXml, DateTimeOffset CreatedAt);

/// <summary><c>GET /assistant/conversations/{id}</c>: the chat and its turns, oldest first.</summary>
public sealed record AssistantConversationHistory(AssistantConversationInfo Conversation, List<AssistantStoredMessage> Messages);
