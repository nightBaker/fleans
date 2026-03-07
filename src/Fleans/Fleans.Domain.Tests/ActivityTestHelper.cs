using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

internal static class ActivityTestHelper
{
    public static ConditionSequenceState CreateEvaluatedConditionState(
        string sequenceFlowId, Guid gatewayInstanceId, bool result)
    {
        var state = new ConditionSequenceState(sequenceFlowId, gatewayInstanceId, Guid.Empty);
        state.SetResult(result);
        return state;
    }

    public static void SetupConditionStates(
        IWorkflowExecutionContext workflowContext,
        Guid activityInstanceId,
        params (string sequenceFlowId, bool result)[] conditions)
    {
        var conditionStates = new Dictionary<Guid, ConditionSequenceState[]>
        {
            [activityInstanceId] = conditions
                .Select(c => CreateEvaluatedConditionState(c.sequenceFlowId, activityInstanceId, c.result))
                .ToArray()
        };
        workflowContext.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(conditionStates));
    }

    public static WorkflowDefinition CreateDefinitionWithSignal(
        List<Activity> activities,
        List<SequenceFlow> sequenceFlows,
        string signalId = "sig1",
        string signalName = "order_shipped")
    {
        return new WorkflowDefinition
        {
            WorkflowId = "test-workflow",
            Activities = activities,
            SequenceFlows = sequenceFlows,
            Signals = [new SignalDefinition(signalId, signalName)]
        };
    }

    public static IWorkflowExecutionContext CreateWorkflowContext(IWorkflowDefinition definition)
    {
        var context = Substitute.For<IWorkflowExecutionContext>();
        context.GetWorkflowInstanceId().Returns(ValueTask.FromResult(Guid.NewGuid()));
        context.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(
                new Dictionary<Guid, ConditionSequenceState[]>()));
        context.GetActiveActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
        context.GetCompletedActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
        context.GetVariable(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns(ValueTask.FromResult<object?>(null));
        return context;
    }

    public static (IActivityExecutionContext context, List<IDomainEvent> publishedEvents)
        CreateActivityContext(string activityId, Guid? instanceId = null)
    {
        var context = Substitute.For<IActivityExecutionContext>();
        var id = instanceId ?? Guid.NewGuid();
        context.GetActivityInstanceId().Returns(ValueTask.FromResult(id));
        context.GetActivityId().Returns(ValueTask.FromResult(activityId));
        context.GetVariablesStateId().Returns(ValueTask.FromResult(Guid.NewGuid()));
        context.IsCompleted().Returns(ValueTask.FromResult(false));

        var publishedEvents = new List<IDomainEvent>();
        context.PublishEvent(Arg.Any<IDomainEvent>())
            .Returns(ValueTask.CompletedTask)
            .AndDoes(ci => publishedEvents.Add(ci.Arg<IDomainEvent>()));

        context.Complete().Returns(ValueTask.CompletedTask);
        context.Execute().Returns(ValueTask.CompletedTask);

        return (context, publishedEvents);
    }

    public static WorkflowDefinition CreateWorkflowDefinition(
        List<Activity> activities,
        List<SequenceFlow> sequenceFlows,
        string workflowId = "test-workflow",
        string? processDefinitionId = "test-process-def")
    {
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = activities,
            SequenceFlows = sequenceFlows,
            ProcessDefinitionId = processDefinitionId
        };
    }
}
