using System.Text.Json;
using AGUI.Abstractions;
using Fleans.Assistant.Contracts;

namespace Fleans.Web.Tests;

/// <summary>AG-UI events (#1054) a stub assistant backend streams, built the way a real backend would.</summary>
internal static class AgUi
{
    public static string Serialize(BaseEvent evt) => JsonSerializer.Serialize(evt, AGUIJsonSerializerContext.Default.BaseEvent);

    public static BaseEvent RunStarted(string threadId = "t1", string runId = "r1") =>
        new RunStartedEvent { ThreadId = threadId, RunId = runId };

    public static BaseEvent Text(string delta, string messageId = "m1") =>
        new TextMessageContentEvent { MessageId = messageId, Delta = delta };

    public static IEnumerable<BaseEvent> Proposal(string bpmnXml) => AssistantAgUi.ProposalEvents(bpmnXml);

    public static BaseEvent Error(string code, string message) => new RunErrorEvent { Code = code, Message = message };

    public static BaseEvent ToolStarted(string id, string name) => new ToolCallStartEvent { ToolCallId = id, ToolCallName = name };

    public static BaseEvent ToolResult(string id) => new ToolCallResultEvent { MessageId = "tm-" + id, ToolCallId = id, Content = "done" };

    public static BaseEvent Approvals(params AssistantApprovalRequest[] approvals) => new RunFinishedEvent
    {
        ThreadId = "t1",
        RunId = "r1",
        Outcome = new RunFinishedInterruptOutcome { Interrupts = approvals.Select(AssistantAgUi.ToInterrupt).ToList() },
    };

    public static BaseEvent Job(AssistantJobUpdate job) => AssistantAgUi.ToJobEvent(job);
}
