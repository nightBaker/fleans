using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

[TestClass]
public class UserTaskRegistrationFailureTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<RegistrationFailureSiloConfigurator>();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegistrationFailure_FollowsErrorPathInsteadOfLeavingAnInvisibleActiveTask(bool hasErrorBoundary)
    {
        var start = new StartEvent("start");
        var task = new UserTask("approval", "alice", [], [], null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "user-task-registration-failure",
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
        if (hasErrorBoundary)
        {
            var boundary = new BoundaryErrorEvent("onError", "approval", null);
            var recovered = new EndEvent("recovered");
            workflow.Activities.Add(boundary);
            workflow.Activities.Add(recovered);
            workflow.SequenceFlows.Add(new SequenceFlow("f3", boundary, recovered));
        }
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        await instance.StartWorkflow(workflow);

        var snapshot = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsNotNull(snapshot);
        Assert.AreEqual(!hasErrorBoundary, snapshot.IsFailed);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsEmpty(snapshot.ActiveActivities);
        var failed = snapshot.CompletedActivities.Single(a => a.ActivityId == "approval");
        Assert.IsNotNull(failed.ErrorState);
        StringAssert.Contains(failed.ErrorState.Message, "user task storage unavailable");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"));
        Assert.AreEqual(hasErrorBoundary, snapshot.CompletedActivities.Any(a => a.ActivityId == "recovered"));
        Assert.IsNull(await QueryService.GetUserTask(failed.ActivityInstanceId));
    }

    private sealed class RegistrationFailureFilter : IIncomingGrainCallFilter
    {
        public Task Invoke(IIncomingGrainCallContext context)
        {
            if (context.Grain is IUserTaskGrain && context.MethodName == nameof(IUserTaskGrain.Register))
                throw new InvalidOperationException("user task storage unavailable");
            return context.Invoke();
        }
    }

    private sealed class RegistrationFailureSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder)
            => hostBuilder.AddIncomingGrainCallFilter<RegistrationFailureFilter>();
    }
}
