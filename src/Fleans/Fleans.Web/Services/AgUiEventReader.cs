using System.Text;
using System.Text.Json;
using AGUI.Abstractions;
using Fleans.Assistant.Contracts;

namespace Fleans.Web.Services;

/// <summary>
/// Turns the AG-UI events of one stream (#1054) into what the assistant panel shows, following the Fleans
/// conventions of <see cref="AssistantAgUi"/>: assistant text, the <see cref="AssistantAgUi.ProposeDiagramTool"/>
/// proposal, other tool calls as activity lines, interrupts as approvals, <see cref="AssistantAgUi.JobEventName"/>
/// custom events as job cards, and <c>RUN_ERROR</c>. Events it does not use are skipped. One instance per stream:
/// it remembers tool call names and argument chunks between events.
/// </summary>
internal sealed class AgUiEventReader
{
    /// <summary>The agent shown on an activity line: AG-UI tool calls do not name one.</summary>
    public const string ActivityAgent = "Assistant";

    private readonly Dictionary<string, string> _toolNames = new();
    private readonly Dictionary<string, StringBuilder> _proposalArgs = new();

    /// <summary>The assistant text read so far.</summary>
    public StringBuilder Text { get; } = new();

    /// <summary>The first complete proposal of the stream; a second one is a backend bug and is ignored.</summary>
    public string? Proposal { get; private set; }

    /// <summary>The <c>RUN_ERROR</c> that ended the run, if any.</summary>
    public AssistantError? Error { get; private set; }

    /// <summary>Parses one SSE <c>data:</c> payload; <c>null</c> for an event type this SDK does not know.</summary>
    /// <exception cref="JsonException">The payload is not an AG-UI event.</exception>
    public static BaseEvent? Parse(string data)
    {
        try
        {
            return JsonSerializer.Deserialize(data, AGUIJsonSerializerContext.Default.BaseEvent)
                ?? throw new JsonException("Empty AG-UI event.");
        }
        catch (AGUIUnknownEventTypeException)
        {
            // The protocol is open: a newer backend may send events this build has no model for.
            return null;
        }
    }

    /// <summary>Reads one event.</summary>
    /// <returns>The text delta it carried (empty when none) and what it reports for the panel.</returns>
    /// <exception cref="JsonException">A proposal's arguments are not the expected JSON.</exception>
    public (string Text, List<AssistantProgress> Progress) Read(BaseEvent evt)
    {
        var progress = new List<AssistantProgress>();
        var text = "";
        switch (evt)
        {
            case TextMessageContentEvent content:
                text = content.Delta ?? "";
                break;
            case TextMessageChunkEvent chunk:
                text = chunk.Delta ?? "";
                break;
            case ToolCallStartEvent start when start.ToolCallName == AssistantAgUi.ProposeDiagramTool:
                _proposalArgs[start.ToolCallId] = new StringBuilder();
                break;
            case ToolCallStartEvent start:
                _toolNames[start.ToolCallId] = start.ToolCallName;
                progress.Add(Activity(start.ToolCallId, start.ToolCallName, AssistantActivityStatus.Started));
                break;
            case ToolCallArgsEvent args when _proposalArgs.TryGetValue(args.ToolCallId, out var buffer):
                buffer.Append(args.Delta);
                break;
            case ToolCallEndEvent end when _proposalArgs.Remove(end.ToolCallId, out var buffer):
                Proposal ??= ProposalOf(buffer.ToString());
                break;
            case ToolCallResultEvent result:
                progress.Add(Activity(result.ToolCallId, _toolNames.GetValueOrDefault(result.ToolCallId, ""), AssistantActivityStatus.Completed));
                break;
            case RunFinishedEvent { Outcome: RunFinishedInterruptOutcome interrupted }:
                foreach (var interrupt in interrupted.Interrupts ?? [])
                    progress.Add(new AssistantProgress(Approval: AssistantAgUi.FromInterrupt(interrupt)));
                break;
            case CustomEvent { Name: AssistantAgUi.JobEventName, Value: { } value }:
                if (value.Deserialize<AssistantJobUpdate>(AssistantProtocol.JsonOptions) is { } job)
                    progress.Add(new AssistantProgress(Job: job));
                break;
            case RunErrorEvent error:
                Error = new AssistantError(
                    string.IsNullOrEmpty(error.Code) ? AssistantErrorCodes.BadResponse : error.Code,
                    string.IsNullOrWhiteSpace(error.Message) ? "The assistant could not answer." : error.Message);
                break;
        }
        Text.Append(text);
        return (text, progress);
    }

    private static AssistantProgress Activity(string id, string tool, string status) =>
        new(Activity: new AssistantActivity(id, ActivityAgent, tool, status, ""));

    private static string ProposalOf(string argumentsJson)
    {
        using var args = JsonDocument.Parse(argumentsJson);
        return args.RootElement.TryGetProperty(AssistantAgUi.BpmnXmlProperty, out var xml) && xml.ValueKind == JsonValueKind.String
            ? xml.GetString() ?? ""
            : throw new JsonException($"The {AssistantAgUi.ProposeDiagramTool} call has no {AssistantAgUi.BpmnXmlProperty}.");
    }
}
