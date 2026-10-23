using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// <see cref="IWorkflowInstanceCallback.FailActivity(string, Guid, Exception, ExpandoObject)"/>:
/// variables reported with a failure (Camunda's <c>bpmnError</c> variables) are merged into the
/// activity's scope before the error propagates.
/// </summary>
[TestClass]
public class FailActivityWithVariablesTests : WorkflowTestBase
{
    [TestMethod]
    public async Task Variables_reported_with_a_failure_reach_the_error_boundary_path()
    {
        // Arrange — start → task1 → end, error boundary on task1 → recovered.
        var instance = await StartWorkflow(withBoundary: true);
        var task = await ActiveEntry(instance, "task1");

        // Act
        await instance.FailActivity("task1", task.ActivityInstanceId,
            new CustomTaskFailedActivityException("FRAUD", "fishy"), Variables(("reason", "card-stolen")));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed, "The boundary caught the failure.");
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("recovered"));
        Assert.AreEqual("FRAUD", snapshot.CompletedActivities.Single(a => a.ActivityId == "task1").ErrorState!.Code);
        Assert.AreEqual("card-stolen", AllVariables(snapshot)["reason"]);
    }

    [TestMethod]
    public async Task Unhandled_failure_with_variables_fails_the_instance_and_keeps_the_variables()
    {
        // Arrange — no boundary.
        var instance = await StartWorkflow(withBoundary: false);
        var task = await ActiveEntry(instance, "task1");

        // Act
        await instance.FailActivity("task1", task.ActivityInstanceId, new Exception("boom"), Variables(("attempt", "3")));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed);
        Assert.AreEqual("500", snapshot.CompletedActivities.Single(a => a.ActivityId == "task1").ErrorState!.Code);
        Assert.AreEqual("3", AllVariables(snapshot)["attempt"]);
    }

    [TestMethod]
    public async Task Failure_with_variables_over_the_size_limit_still_fails_the_activity_without_them()
    {
        // Arrange — no boundary.
        var instance = await StartWorkflow(withBoundary: false);
        var task = await ActiveEntry(instance, "task1");

        // Act — rejecting the failure would leave task1 running forever, so only the variables go
        await instance.FailActivity("task1", task.ActivityInstanceId, new Exception("boom"),
            Variables(("blob", new string('x', TestMaxVariablesSizeBytes))));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed);
        Assert.IsFalse(AllVariables(snapshot).ContainsKey("blob"));
    }

    [TestMethod]
    public async Task Redelivered_failure_with_variables_is_ignored()
    {
        // Arrange — the activity already failed into its boundary path.
        var instance = await StartWorkflow(withBoundary: true);
        var task = await ActiveEntry(instance, "task1");
        await instance.FailActivity("task1", task.ActivityInstanceId,
            new CustomTaskFailedActivityException("FRAUD", "fishy"), Variables(("reason", "first")));

        // Act — the same report again (at-least-once delivery), with different variables.
        await instance.FailActivity("task1", task.ActivityInstanceId,
            new CustomTaskFailedActivityException("FRAUD", "fishy"), Variables(("reason", "second")));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.AreEqual("first", AllVariables(snapshot)["reason"]);
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "recovered"));
    }

    [TestMethod]
    public async Task Variables_reach_an_error_event_sub_process_that_catches_a_failure_from_a_parallel_branch()
    {
        // Arrange — start → fork → (taskA | taskB); a process-level error event sub-process
        // catching FRAUD waits in handlerTask. Branches run on cloned scopes, which the handler
        // (a child of the root scope) cannot see.
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var taskA = new TaskActivity("taskA");
        var taskB = new TaskActivity("taskB");
        var endA = new EndEvent("endA");
        var endB = new EndEvent("endB");
        var errStart = new ErrorStartEvent("esp_errStart", "FRAUD");
        var handlerTask = new TaskActivity("handlerTask");
        var errEnd = new EndEvent("esp_end");
        var esp = new EventSubProcess("esp")
        {
            Activities = [errStart, handlerTask, errEnd],
            SequenceFlows = [new SequenceFlow("esp_f1", errStart, handlerTask), new SequenceFlow("esp_f2", handlerTask, errEnd)],
            IsInterrupting = true,
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(new WorkflowDefinition
        {
            WorkflowId = "fail-with-variables-esp",
            Activities = [start, fork, taskA, taskB, endA, endB, esp],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, taskA),
                new SequenceFlow("f3", fork, taskB),
                new SequenceFlow("f4", taskA, endA),
                new SequenceFlow("f5", taskB, endB),
            ],
        });
        var task = await ActiveEntry(instance, "taskA");

        // Act
        await instance.FailActivity("taskA", task.ActivityInstanceId,
            new CustomTaskFailedActivityException("FRAUD", "fishy"), Variables(("reason", "card-stolen")));

        // Assert — the handler's own scope holds the variable.
        var handler = await ActiveEntry(instance, "handlerTask");
        var handlerVariables = (IDictionary<string, object?>)await instance.GetVariables(handler.VariablesStateId);
        Assert.AreEqual("card-stolen", handlerVariables["reason"]);
    }

    [TestMethod]
    public async Task Empty_variables_behave_like_the_overload_without_variables()
    {
        var instance = await StartWorkflow(withBoundary: true);
        var task = await ActiveEntry(instance, "task1");

        await instance.FailActivity("task1", task.ActivityInstanceId, new Exception("boom"), null);

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("recovered"));
    }

    private async Task<IWorkflowInstanceGrain> StartWorkflow(bool withBoundary)
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        var activities = new List<Activity> { start, task, end };
        var flows = new List<SequenceFlow> { new("f1", start, task), new("f2", task, end) };
        if (withBoundary)
        {
            var boundary = new BoundaryErrorEvent("boundary1", "task1", null);
            var recovered = new EndEvent("recovered");
            activities.AddRange([boundary, recovered]);
            flows.Add(new SequenceFlow("f3", boundary, recovered));
        }

        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(new WorkflowDefinition
        {
            WorkflowId = "fail-with-variables",
            Activities = activities,
            SequenceFlows = flows,
        });
        return instance;
    }

    private async Task<Fleans.Application.QueryModels.ActivityInstanceSnapshot> ActiveEntry(IWorkflowInstanceGrain instance, string activityId)
    {
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivities.Any(a => a.ActivityId == activityId));
        return snapshot.ActiveActivities.Single(a => a.ActivityId == activityId);
    }

    private static Dictionary<string, string> AllVariables(Fleans.Application.QueryModels.InstanceStateSnapshot snapshot) =>
        snapshot.VariableStates.SelectMany(vs => vs.Variables).ToDictionary(kv => kv.Key, kv => kv.Value);

    private static ExpandoObject Variables(params (string Name, object? Value)[] values)
    {
        var expando = new ExpandoObject();
        foreach (var (name, value) in values)
            ((IDictionary<string, object?>)expando)[name] = value;
        return expando;
    }
}
