using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.ExternalTasks.Sdk;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// External workers (#979): a routed task type goes to the external-task stream instead of the
/// plugin stream — a connected gRPC worker gets its evaluated inputs — and the task can also be
/// completed or failed by activity instance id (the REST path), idempotently.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ExternalTaskTests : WorkflowTestBase
{
    // Listed in WorkflowTestBase.TestExternalTaskTypes.
    private const string ExternalType = "ext-charge";

    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    [TestMethod]
    public async Task Routed_task_type_reaches_a_grpc_worker_with_its_inputs()
    {
        // Arrange
        await Deploy("ext-worker", ExternalType);
        await using var workers = new GrpcExternalTaskWorkers(Cluster.Client, TestWorkerHeartbeatInterval, TestExternalTaskExecutionTimeout);
        await workers.StartAsync("charge-worker", sv => sv.AddExternalTaskHandler<ChargeHandler>(ExternalType));
        await workers.WaitForCatalogWorkers(ExternalType, "charge-worker");

        // Act
        var instanceId = await CommandService.StartWorkflow("ext-worker", Vars(("count", 2L)), businessId: "ORDER-7");

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual("20", Variable(snapshot, "charged"));
        Assert.AreEqual("ORDER-7", Variable(snapshot, "businessId"));
    }

    [TestMethod]
    public async Task Routed_task_waits_while_no_worker_is_connected()
    {
        // Arrange
        await Deploy("ext-waits", ExternalType);

        // Act
        var instanceId = await CommandService.StartWorkflow("ext-waits", Vars(("count", 1L)));

        // Assert — no plugin handles the type, so the task stays active for a worker
        var activityInstanceId = await WaitForActiveTask(instanceId);
        Assert.AreNotEqual(Guid.Empty, activityInstanceId);
    }

    [TestMethod]
    public async Task Completion_by_activity_instance_id_completes_the_task_and_a_repeat_is_ignored()
    {
        // Arrange
        await Deploy("ext-complete", ExternalType);
        var instanceId = await CommandService.StartWorkflow("ext-complete", Vars(("count", 1L)));
        var activityInstanceId = await WaitForActiveTask(instanceId);

        // Act — at-least-once delivery: the worker reports twice
        await CommandService.CompleteActivity(instanceId, "charge", activityInstanceId, Vars(("paymentRef", "ch_1")));
        await CommandService.CompleteActivity(instanceId, "charge", activityInstanceId, Vars(("paymentRef", "ch_2")));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("end"));
        Assert.AreEqual("ch_1", Variable(snapshot, "paymentRef"));
    }

    [TestMethod]
    public async Task Failure_by_activity_instance_id_with_an_error_code_fails_the_task()
    {
        // Arrange
        await Deploy("ext-fail", ExternalType);
        var instanceId = await CommandService.StartWorkflow("ext-fail", Vars(("count", 1L)));
        var activityInstanceId = await WaitForActiveTask(instanceId);

        // Act
        await CommandService.FailActivity(instanceId, "charge", activityInstanceId,
            "PAYMENT_DECLINED", "card declined", Vars(("reason", "insufficient-funds")));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed);
        Assert.AreEqual("PAYMENT_DECLINED",
            snapshot.CompletedActivities.Single(a => a.ActivityId == "charge").ErrorState!.Code);
    }

    [TestMethod]
    public async Task Unrouted_task_type_still_goes_to_its_plugin()
    {
        // Arrange — test-no-op-a is a test plugin that completes at once
        await Deploy("ext-plugin", "test-no-op-a");

        // Act
        var instanceId = await CommandService.StartWorkflow("ext-plugin", Vars(("count", 1L)));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
    }

    private Task Deploy(string workflowId, string taskType)
    {
        var start = new StartEvent("start");
        var charge = new CustomTaskActivity("charge", taskType)
        {
            InputMappings = [new InputMapping("=count * 10", "amount")]
        };
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, charge, end],
            SequenceFlows = [new SequenceFlow("f1", start, charge), new SequenceFlow("f2", charge, end)]
        };
        return Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflowId)
            .DeployVersion(workflow, "<placeholder/>");
    }

    private async Task<Guid> WaitForActiveTask(Guid instanceId)
    {
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == "charge"));
        return snapshot.ActiveActivities.Single(a => a.ActivityId == "charge").ActivityInstanceId;
    }

    private static string? Variable(QueryModels.InstanceStateSnapshot snapshot, string name) =>
        snapshot.VariableStates.SelectMany(v => v.Variables).FirstOrDefault(kv => kv.Key == name).Value;

    private static ExpandoObject Vars(params (string Key, object Value)[] values)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in values)
            ((IDictionary<string, object?>)vars)[key] = value;
        return vars;
    }

    /// <summary>Echoes the evaluated <c>amount</c> input and the business id it was given.</summary>
    private sealed class ChargeHandler : ExternalTaskHandler
    {
        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken) =>
            Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>
            {
                ["charged"] = resolvedInputs["amount"]?.ToString(),
                ["businessId"] = context.BusinessId,
            });
    }
}
