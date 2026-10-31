using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// External workers through a queue (#979): a routed task type goes to <see cref="IExternalTaskQueue"/>
/// instead of the plugin stream, the worker completes or fails it by activity instance id, and a
/// failed publish fails the task.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ExternalTaskTests : WorkflowTestBase
{
    private const string ExternalType = "ext-charge";

    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<ExternalTaskSiloConfigurator>();

    [TestInitialize]
    public void ClearQueue()
    {
        RecordingQueue.Published.Clear();
        RecordingQueue.FailPublish = false;
    }

    [TestMethod]
    public async Task Routed_task_type_is_published_to_the_queue_with_its_inputs()
    {
        // Arrange
        await Deploy("ext-publish", ExternalType);

        // Act
        var instanceId = await CommandService.StartWorkflow("ext-publish", Vars(("count", 2L)), businessId: "ORDER-7");
        var task = await WaitForPublished(instanceId);

        // Assert
        Assert.AreEqual("charge", task.ActivityId);
        Assert.AreEqual(ExternalType, task.TaskType);
        Assert.AreEqual("ORDER-7", task.BusinessId);
        Assert.AreEqual(20L, Convert.ToInt64(((IDictionary<string, object?>)task.Inputs)["amount"]));
        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.IsTrue(snapshot!.ActiveActivities.Any(a => a.ActivityId == "charge"), "the task waits for its worker");
    }

    [TestMethod]
    public async Task Worker_completion_by_activity_instance_id_completes_the_task_and_a_repeat_is_ignored()
    {
        // Arrange
        await Deploy("ext-complete", ExternalType);
        var instanceId = await CommandService.StartWorkflow("ext-complete", Vars(("count", 1L)));
        var task = await WaitForPublished(instanceId);

        // Act — at-least-once delivery: the worker reports twice
        await CommandService.CompleteActivity(instanceId, "charge", task.ActivityInstanceId, Vars(("paymentRef", "ch_1")));
        await CommandService.CompleteActivity(instanceId, "charge", task.ActivityInstanceId, Vars(("paymentRef", "ch_2")));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.IsTrue(snapshot.CompletedActivityIds.Contains("end"));
        Assert.AreEqual("ch_1", snapshot.VariableStates.SelectMany(v => v.Variables).Single(kv => kv.Key == "paymentRef").Value);
    }

    [TestMethod]
    public async Task Worker_failure_with_an_error_code_fails_the_task()
    {
        // Arrange
        await Deploy("ext-fail", ExternalType);
        var instanceId = await CommandService.StartWorkflow("ext-fail", Vars(("count", 1L)));
        var task = await WaitForPublished(instanceId);

        // Act
        await CommandService.FailActivity(instanceId, "charge", task.ActivityInstanceId,
            "PAYMENT_DECLINED", "card declined", Vars(("reason", "insufficient-funds")));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed);
        Assert.AreEqual("PAYMENT_DECLINED",
            snapshot.CompletedActivities.Single(a => a.ActivityId == "charge").ErrorState!.Code);
    }

    [TestMethod]
    public async Task Failed_publish_fails_the_task_instead_of_leaving_it_waiting()
    {
        // Arrange — the queue rejects the task (broker down)
        await Deploy("ext-publish-fails", ExternalType);
        RecordingQueue.FailPublish = true;

        // Act
        var instanceId = await CommandService.StartWorkflow("ext-publish-fails", Vars(("count", 1L)));

        // Assert
        var snapshot = await WaitForCondition(instanceId, s => s.IsCompleted);
        Assert.IsTrue(snapshot.IsFailed);
        Assert.IsNotNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "charge").ErrorState);
        Assert.IsFalse(snapshot.CompletedActivityIds.Contains("end"));
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
        Assert.IsEmpty(Published(instanceId));
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

    private static List<ExecuteCustomTaskEvent> Published(Guid instanceId) =>
        RecordingQueue.Published.Where(t => t.WorkflowInstanceId == instanceId).ToList();

    private static async Task<ExecuteCustomTaskEvent> WaitForPublished(Guid instanceId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (Published(instanceId) is [var task, ..])
                return task;
            Assert.IsTrue(DateTime.UtcNow < deadline, "the task was not published to the external queue");
            await Task.Delay(25);
        }
    }

    private static ExpandoObject Vars(params (string Key, object Value)[] values)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in values)
            ((IDictionary<string, object?>)vars)[key] = value;
        return vars;
    }

    private sealed class RecordingQueue : IExternalTaskQueue
    {
        public static readonly ConcurrentQueue<ExecuteCustomTaskEvent> Published = new();
        public static volatile bool FailPublish;

        public Task Publish(ExecuteCustomTaskEvent task, CancellationToken cancellationToken)
        {
            if (FailPublish)
                throw new InvalidOperationException("broker unavailable");
            Published.Enqueue(task);
            return Task.CompletedTask;
        }
    }

    private sealed class ExternalTaskSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<IExternalTaskQueue, RecordingQueue>();
                services.Configure<ExternalTaskOptions>(o =>
                {
                    o.Provider = "test";
                    o.TaskTypes = [ExternalType];
                });
            });
    }
}
