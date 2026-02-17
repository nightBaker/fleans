using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using NSubstitute;

namespace Fleans.Domain.Tests;

internal static class ActivityTestHelper
{
    public static IWorkflowExecutionContext CreateWorkflowContext(IWorkflowDefinition definition)
    {
        var context = Substitute.For<IWorkflowExecutionContext>();
        context.GetWorkflowDefinition().Returns(ValueTask.FromResult(definition));
        context.GetWorkflowInstanceId().Returns(ValueTask.FromResult(Guid.NewGuid()));
        context.GetConditionSequenceStates()
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, ConditionSequenceState[]>>(
                new Dictionary<Guid, ConditionSequenceState[]>()));
        context.GetActiveActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
        context.GetCompletedActivities()
            .Returns(ValueTask.FromResult<IReadOnlyList<IActivityExecutionContext>>([]));
        context.StartChildWorkflow(Arg.Any<Activities.CallActivity>(), Arg.Any<IActivityExecutionContext>())
            .Returns(ValueTask.CompletedTask);
        return context;
    }

    public static (IActivityExecutionContext context, List<IDomainEvent> publishedEvents)
        CreateActivityContext(string activityId, Guid? instanceId = null)
    {
        var context = Substitute.For<IActivityExecutionContext>();
        var id = instanceId ?? Guid.NewGuid();
        context.GetActivityInstanceId().Returns(ValueTask.FromResult(id));
        context.GetActivityId().Returns(ValueTask.FromResult(activityId));
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
