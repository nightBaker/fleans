using System.Globalization;
using System.Text.Json;
using AGUI.Abstractions;

namespace Fleans.Assistant.Contracts;

/// <summary>
/// How the editor's assistant run maps onto the AG-UI protocol (#1054, https://docs.ag-ui.com). The run is a
/// standard AG-UI <c>RunAgentInput</c> answered with a standard AG-UI event stream; this class names the few
/// Fleans conventions on top and converts between them and the editor's records, so a .NET backend can share them.
/// <list type="bullet">
/// <item>The diagram is the run's <c>state</c>: <c>{"bpmnXml": "…"}</c> (absent for an empty diagram).</item>
/// <item>The engine version and the plugin catalog are two <c>context</c> entries (<see cref="EngineVersionContext"/>,
/// <see cref="PluginsContext"/>).</item>
/// <item>A proposal is a call of the frontend tool <see cref="ProposeDiagramTool"/> (<c>{"bpmnXml": "…"}</c>), which
/// the editor declares in <c>tools</c>; it shows the diagram for the user to apply and never answers the call.</item>
/// <item>Any other tool call is shown as an activity line; an approval is an interrupt; a job update is a
/// <c>CUSTOM</c> event named <see cref="JobEventName"/>.</item>
/// </list>
/// </summary>
public static class AssistantAgUi
{
    /// <summary>The frontend tool a backend calls to propose a complete BPMN 2.0 diagram (at most one per run).</summary>
    public const string ProposeDiagramTool = "propose_bpmn_diagram";

    /// <summary>The property holding BPMN XML in the run's <c>state</c> and in <see cref="ProposeDiagramTool"/>'s arguments.</summary>
    public const string BpmnXmlProperty = "bpmnXml";

    /// <summary>The <c>context</c> entry whose value is the Fleans engine version.</summary>
    public const string EngineVersionContext = "fleans.engineVersion";

    /// <summary>The <c>context</c> entry whose value is the JSON array of <see cref="AssistantPlugin"/>s.</summary>
    public const string PluginsContext = "fleans.plugins";

    /// <summary>The name of the <c>CUSTOM</c> event whose value is an <see cref="AssistantJobUpdate"/>.</summary>
    public const string JobEventName = "fleans.job";

    /// <summary>The interrupt <c>metadata</c> key holding <see cref="AssistantApprovalRequest.DetailsMarkdown"/>.</summary>
    public const string DetailsMarkdownMetadata = "detailsMarkdown";

    private static readonly JsonElement ProposeDiagramParameters = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            [BpmnXmlProperty] = new
            {
                type = "string",
                description = "A complete BPMN 2.0 XML document. Diagram interchange (bpmndi) is optional: the editor lays out a bare process.",
            },
        },
        required = new[] { BpmnXmlProperty },
    });

    /// <summary>The <see cref="ProposeDiagramTool"/> declaration the editor sends in every run's <c>tools</c>.</summary>
    public static AGUITool ProposeDiagram() => new()
    {
        Name = ProposeDiagramTool,
        Description = "Propose a complete BPMN 2.0 diagram. The editor shows it to the user, who applies or discards it.",
        Parameters = ProposeDiagramParameters,
    };

    /// <summary>The run the editor posts for <paramref name="request"/>.</summary>
    /// <param name="runId">The run's id; a new one when <c>null</c>.</param>
    public static RunAgentInput ToRunAgentInput(AssistantRequest request, string? runId = null)
    {
        var messages = new List<AGUIMessage>();
        foreach (var message in request.Messages)
        {
            var id = NewId();
            messages.Add(message.Role == AssistantRoles.Assistant
                ? new AGUIAssistantMessage { Id = id, Content = message.Text }
                : new AGUIUserMessage { Id = id, Content = message.Text });
        }

        var input = new RunAgentInput
        {
            ThreadId = request.ConversationId ?? NewId(),
            RunId = runId ?? NewId(),
            ProtocolVersion = AGUIProtocol.Version,
            Messages = messages,
            Tools = new List<AGUITool> { ProposeDiagram() },
            Context = new List<AGUIContext>
            {
                new() { Description = EngineVersionContext, Value = request.EngineVersion },
                new() { Description = PluginsContext, Value = JsonSerializer.Serialize(request.Plugins, AssistantProtocol.JsonOptions) },
            },
            State = request.CurrentXml is null
                ? JsonSerializer.SerializeToElement(new Dictionary<string, string>())
                : JsonSerializer.SerializeToElement(new Dictionary<string, string> { [BpmnXmlProperty] = request.CurrentXml }),
        };
        if (request.Approvals is { Count: > 0 } approvals)
            input.Resume = approvals.Select(ToResume).ToList();
        return input;
    }

    /// <summary>
    /// The editor's request inside a run, for a .NET backend. <see cref="AssistantRequest.ConversationId"/> is the
    /// run's <c>threadId</c>, which is a fresh id for a stateless run: a backend that stores chats knows its own.
    /// </summary>
    public static AssistantRequest FromRunAgentInput(RunAgentInput input)
    {
        var messages = new List<AssistantMessage>();
        foreach (var message in input.Messages ?? [])
        {
            switch (message)
            {
                case AGUIUserMessage user:
                    messages.Add(new AssistantMessage(AssistantRoles.User, user.Content.ToString() ?? ""));
                    break;
                case AGUIAssistantMessage assistant:
                    messages.Add(new AssistantMessage(AssistantRoles.Assistant, assistant.Content ?? ""));
                    break;
            }
        }

        string? Context(string name) => input.Context?.FirstOrDefault(c => c.Description == name)?.Value;
        var plugins = Context(PluginsContext) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<AssistantPlugin>>(json, AssistantProtocol.JsonOptions) ?? []
            : [];
        string? currentXml = null;
        if (input.State is { ValueKind: JsonValueKind.Object } state
            && state.TryGetProperty(BpmnXmlProperty, out var xml) && xml.ValueKind == JsonValueKind.String)
            currentXml = xml.GetString();

        return new AssistantRequest(
            messages,
            currentXml,
            plugins,
            Context(EngineVersionContext) ?? "",
            input.ThreadId,
            input.Resume is { Count: > 0 } resume ? resume.Select(FromResume).ToList() : null);
    }

    /// <summary>The <c>resume</c> entry answering an approval: <c>resolved</c> with <c>{"approved": …, "comment": …}</c>.</summary>
    public static AGUIResume ToResume(AssistantApprovalDecision decision) => new()
    {
        InterruptId = decision.ActionId,
        Status = ResumeStatus.Resolved,
        Payload = JsonSerializer.SerializeToElement(new { approved = decision.Approved, comment = decision.Comment }),
    };

    /// <summary>The approval a <c>resume</c> entry answers; <c>cancelled</c> or a payload without <c>approved: true</c> is a refusal.</summary>
    public static AssistantApprovalDecision FromResume(AGUIResume resume)
    {
        var approved = false;
        string? comment = null;
        if (resume.Status == ResumeStatus.Resolved && resume.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            approved = payload.TryGetProperty("approved", out var a) && a.ValueKind == JsonValueKind.True;
            if (payload.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String)
                comment = c.GetString();
        }
        return new AssistantApprovalDecision(resume.InterruptId, approved, comment);
    }

    /// <summary>The interrupt a backend puts in <c>RUN_FINISHED</c>'s <c>interrupt</c> outcome to ask for an approval.</summary>
    public static AGUIInterrupt ToInterrupt(AssistantApprovalRequest approval) => new()
    {
        Id = approval.ActionId,
        Reason = approval.Kind,
        Message = approval.Summary,
        ExpiresAt = approval.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
        Metadata = JsonSerializer.SerializeToElement(new Dictionary<string, string> { [DetailsMarkdownMetadata] = approval.DetailsMarkdown }),
    };

    /// <summary>
    /// The approval an interrupt asks for: its <c>reason</c> is the kind, its <c>message</c> the summary. An interrupt
    /// without a readable <c>expiresAt</c> never expires.
    /// </summary>
    public static AssistantApprovalRequest FromInterrupt(AGUIInterrupt interrupt)
    {
        var details = "";
        if (interrupt.Metadata is { ValueKind: JsonValueKind.Object } metadata
            && metadata.TryGetProperty(DetailsMarkdownMetadata, out var d) && d.ValueKind == JsonValueKind.String)
            details = d.GetString() ?? "";
        var expiresAt = DateTimeOffset.TryParse(interrupt.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : DateTimeOffset.MaxValue;
        return new AssistantApprovalRequest(interrupt.Id, interrupt.Reason ?? "", interrupt.Message ?? interrupt.Reason ?? "", details, expiresAt);
    }

    /// <summary>The <c>CUSTOM</c> event reporting a job update.</summary>
    public static CustomEvent ToJobEvent(AssistantJobUpdate job) => new()
    {
        Name = JobEventName,
        Value = JsonSerializer.SerializeToElement(job, AssistantProtocol.JsonOptions),
    };

    /// <summary>The <c>TOOL_CALL_START</c> / <c>ARGS</c> / <c>END</c> events of a <see cref="ProposeDiagramTool"/> call.</summary>
    public static IEnumerable<BaseEvent> ProposalEvents(string bpmnXml, string? toolCallId = null)
    {
        var id = toolCallId ?? NewId();
        yield return new ToolCallStartEvent { ToolCallId = id, ToolCallName = ProposeDiagramTool };
        yield return new ToolCallArgsEvent
        {
            ToolCallId = id,
            Delta = JsonSerializer.Serialize(new Dictionary<string, string> { [BpmnXmlProperty] = bpmnXml }),
        };
        yield return new ToolCallEndEvent { ToolCallId = id };
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
