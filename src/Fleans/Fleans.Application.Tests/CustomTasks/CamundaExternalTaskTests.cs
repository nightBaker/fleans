using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.Plugins.CamundaExternalTask;
using Microsoft.EntityFrameworkCore;

namespace Fleans.Application.Tests.CustomTasks;

/// <summary>
/// End-to-end through the real engine: a <c>camunda:&lt;topic&gt;</c> service task is queued by the
/// plugin's stream handler, fetched and locked through <see cref="ExternalTaskService"/> (the code
/// behind the Camunda REST API) and reported back into the workflow.
/// </summary>
[TestClass]
[DoNotParallelize]
public class CamundaExternalTaskTests : WorkflowTestBase
{
    private const string Topic = "charge-card";
    private const string Worker = "worker-1";

    // CamundaExternalTaskHandler.LogSkippedInactive
    private const int SkippedInactiveEventId = 9351;

    private ExternalTaskService Service => GetSiloService<ExternalTaskService>();

    [TestInitialize]
    public async Task ClearExternalTasks()
    {
        // The shared in-memory SQLite database can outlive a test through pooled connections.
        await using var db = await GetSiloService<IDbContextFactory<ExternalTaskDbContext>>().CreateDbContextAsync();
        await db.ExternalTasks.ExecuteDeleteAsync();
    }

    [TestMethod]
    public async Task Complete_passes_variables_both_ways_and_finishes_the_workflow()
    {
        // Arrange
        var instance = await StartInstance(ChargeWorkflow(
            inputs: [new InputMapping("=amount", "amountToCharge")]));

        // Act
        var task = (await FetchOne()).Single();
        await Service.Complete(task.Id, new CompleteExternalTaskDto(Worker,
            new Dictionary<string, VariableValueDto> { ["charged"] = new(true, "Boolean", null) },
            LocalVariables: null));

        // Assert — fetched task carries Camunda-shaped metadata and typed variables.
        Assert.AreEqual(Topic, task.TopicName);
        Assert.AreEqual(Worker, task.WorkerId);
        Assert.AreEqual(instance.GetPrimaryKey().ToString("D"), task.ProcessInstanceId);
        Assert.AreEqual("ct1", task.ActivityId);
        Assert.AreEqual("Integer", task.Variables["amount"].Type);
        Assert.AreEqual(42, task.Variables["amount"].Value);
        Assert.AreEqual(42, task.Variables["amountToCharge"].Value, "Input mappings surface as task variables.");
        Assert.AreEqual("String", task.Variables["customer"].Type);

        // Assert — the workflow completed with the worker's variables merged in.
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");
        Assert.IsNull(ct1.ErrorState);
        var variables = (IDictionary<string, object?>)await instance.GetVariables(ct1.VariablesStateId);
        Assert.AreEqual(true, variables["charged"]);
        Assert.AreEqual(0L, await Service.Count(ForInstance(instance)), "A completed task leaves the table.");
    }

    [TestMethod]
    public async Task Failure_with_retries_refetches_and_exhausted_retries_fail_the_activity()
    {
        // Arrange
        var instance = await StartInstance(ChargeWorkflow());
        var first = (await FetchOne()).Single();

        // Act 1 — fail with a retry left; retryTimeout 0 makes it fetchable again at once.
        await Service.HandleFailure(first.Id, new ExternalTaskFailureDto(Worker, "card service down", "stack", 1, 0));
        var retry = (await FetchOne()).Single();

        // Assert 1
        Assert.AreEqual(first.Id, retry.Id);
        Assert.AreEqual(1, retry.Retries);
        Assert.AreEqual("card service down", retry.ErrorMessage);
        Assert.AreEqual("stack", retry.ErrorDetails);

        // Act 2 — no retries left: Fleans fails the activity (Camunda would raise an incident).
        await Service.HandleFailure(retry.Id, new ExternalTaskFailureDto(Worker, "still down", null, 0, 0));

        // Assert 2
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");
        Assert.IsNotNull(ct1.ErrorState);
        Assert.AreEqual(ExternalTaskService.RetriesExhaustedErrorCode, ct1.ErrorState.Code);
        Assert.AreEqual("still down", ct1.ErrorState.Message);
        Assert.AreEqual(0L, await Service.Count(ForInstance(instance)));
    }

    [TestMethod]
    public async Task BpmnError_is_caught_by_the_error_boundary_with_the_same_code()
    {
        // Arrange — ct1 has an error boundary for PAYMENT_DECLINED → declined end.
        var instance = await StartInstance(ChargeWorkflow(boundaryErrorCode: "PAYMENT_DECLINED"));
        var task = (await FetchOne()).Single();

        // Act
        await Service.HandleBpmnError(task.Id,
            new ExternalTaskBpmnErrorDto(Worker, "PAYMENT_DECLINED", "insufficient funds", null));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("declined"));
        Assert.IsFalse(snapshot.CompletedActivityIds.Contains("end"));
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");
        Assert.AreEqual("PAYMENT_DECLINED", ct1.ErrorState?.Code);
        Assert.AreEqual(0L, await Service.Count(ForInstance(instance)));
    }

    [TestMethod]
    public async Task Another_worker_cannot_complete_or_fetch_a_locked_task()
    {
        // Arrange
        var instance = await StartInstance(ChargeWorkflow());
        var task = (await FetchOne()).Single();

        // Act
        var rejected = await Assert.ThrowsExactlyAsync<ExternalTaskApiException>(() =>
            Service.Complete(task.Id, new CompleteExternalTaskDto("worker-2", null, null)));
        var fetchedByOther = await Service.FetchAndLock(FetchRequest("worker-2", asyncResponseTimeout: null), default);

        // Assert
        Assert.AreEqual(500, rejected.StatusCode);
        Assert.AreEqual(0, fetchedByOther.Count, "A locked task is invisible to other workers.");
        var snapshot = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsTrue(snapshot!.ActiveActivityIds.Contains("ct1"), "The activity keeps waiting for its worker.");

        // Unlock hands it over.
        await Service.Unlock(task.Id);
        Assert.AreEqual(task.Id, (await Service.FetchAndLock(FetchRequest("worker-2", null), default)).Single().Id);
    }

    [TestMethod]
    public async Task Redelivered_event_after_completion_does_not_requeue_the_task()
    {
        // Arrange — run the task to completion.
        var workflow = ChargeWorkflow();
        var instance = await StartInstance(workflow);
        var task = (await FetchOne()).Single();
        await Service.Complete(task.Id, new CompleteExternalTaskDto(Worker, null, null));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");

        // Act — the stream redelivers the original event.
        CapturedLogEvents.Clear();
        await Cluster.GrainFactory.GetGrain<IEventPublisher>(0).Publish(new ExecuteCustomTaskEvent(
            WorkflowInstanceId: instance.GetPrimaryKey(),
            WorkflowId: workflow.WorkflowId,
            ProcessDefinitionId: null,
            ActivityInstanceId: ct1.ActivityInstanceId,
            ActivityId: "ct1",
            TaskType: CamundaExternalTaskConventions.TaskTypeFor(Topic),
            InputMappings: new List<InputMapping>(),
            OutputMappings: new List<OutputMapping>(),
            VariablesId: ct1.VariablesStateId));
        await CapturedLogEvents.WaitFor<CamundaExternalTaskHandler>(SkippedInactiveEventId, 1);

        // Assert
        Assert.AreEqual(0L, await Service.Count(ForInstance(instance)));
    }

    private static ExternalTaskQuery ForInstance(IWorkflowInstanceGrain instance) =>
        new(ProcessInstanceId: instance.GetPrimaryKey().ToString("D"));

    private async Task<IWorkflowInstanceGrain> StartInstance(WorkflowDefinition workflow)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.SetWorkflow(workflow);
        dynamic variables = new ExpandoObject();
        variables.amount = 42;
        variables.customer = "bob";
        await instance.SetInitialVariables(variables);
        await instance.StartWorkflow();
        return instance;
    }

    /// <summary>Long-polls like a real Camunda client: the stream delivers the task asynchronously.</summary>
    private Task<List<LockedExternalTaskDto>> FetchOne() =>
        Service.FetchAndLock(FetchRequest(Worker, asyncResponseTimeout: 10_000), default);

    private static FetchExternalTasksDto FetchRequest(string workerId, long? asyncResponseTimeout) =>
        new(workerId, MaxTasks: 1, UsePriority: false, asyncResponseTimeout,
            [new FetchExternalTaskTopicDto(Topic, LockDuration: 60_000, Variables: null, LocalVariables: false,
                null, null, null, null)]);

    private static WorkflowDefinition ChargeWorkflow(
        List<InputMapping>? inputs = null, string? boundaryErrorCode = null)
    {
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", CamundaExternalTaskConventions.TaskTypeFor(Topic), inputs, null);
        var end = new EndEvent("end");
        var activities = new List<Activity> { start, ct1, end };
        var flows = new List<SequenceFlow>
        {
            new("f1", start, ct1),
            new("f2", ct1, end),
        };
        if (boundaryErrorCode is not null)
        {
            var boundary = new BoundaryErrorEvent("b1", "ct1", boundaryErrorCode);
            var declined = new EndEvent("declined");
            activities.Add(boundary);
            activities.Add(declined);
            flows.Add(new SequenceFlow("f3", boundary, declined));
        }

        return new WorkflowDefinition
        {
            WorkflowId = "camunda-external-task",
            Activities = activities,
            SequenceFlows = flows,
        };
    }
}
