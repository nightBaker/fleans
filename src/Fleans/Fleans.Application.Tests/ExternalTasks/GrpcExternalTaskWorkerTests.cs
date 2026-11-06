using Fleans.Application.CustomTasks;
using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Errors;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// External tasks end to end over the worker protocol: the engine (TestCluster) publishes the task,
/// <see cref="ExternalTaskDispatcherGrain"/> assigns it through <see cref="ExternalTaskWorkerGrpcService"/>
/// (hosted on an in-process TestServer, as Fleans.Api hosts it) to a real Fleans.ExternalTasks.Sdk worker,
/// whose outcome completes or fails the activity.
/// </summary>
[TestClass]
[DoNotParallelize]
public class GrpcExternalTaskWorkerTests : WorkflowTestBase
{
    // ExternalTaskDispatcherGrain.LogSkippedInactiveActivity — the positive signal that a duplicate was handled.
    private const int SkippedInactiveActivityEventId = 14508;

    private GrpcExternalTaskWorkers _workers = null!;

    [TestInitialize]
    public void StartEngine()
    {
        TestHandlers.Reset();
        _workers = new GrpcExternalTaskWorkers(Cluster.Client, TestWorkerHeartbeatInterval, TestExternalTaskExecutionTimeout);
    }

    [TestCleanup]
    public async Task StopHosts()
    {
        TestHandlers.Gate.TrySetResult();
        await _workers.DisposeAsync();
    }

    [TestMethod]
    [DataRow(false, DisplayName = "gRPC")]
    [DataRow(true, DisplayName = "gRPC-Web")]
    public async Task Task_runs_on_the_connected_worker_and_its_result_completes_the_activity(bool grpcWeb)
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>("grpc-echo"), grpcWeb);
        await WaitForCatalogWorkers("grpc-echo", "worker-a");
        var workflow = SingleCustomTaskWorkflow("grpc-echo-completes", "grpc-echo");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);

        // Assert — state after completion
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "ct1");
        Assert.AreEqual("worker-a", Variable(snapshot, "echoedBy"));
        var execution = TestHandlers.Executions.Single();
        Assert.AreEqual(instance.GetPrimaryKey(), execution.Context.WorkflowInstanceId);
        Assert.AreEqual("ct1", execution.Context.ActivityId);
        Assert.AreEqual("grpc-echo", execution.Context.TaskType);
    }

    [TestMethod]
    public async Task Typed_handler_failure_fails_the_activity_with_its_error_code()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<FailingHandler>("grpc-failing"));
        await WaitForCatalogWorkers("grpc-failing", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-failing", "grpc-failing"));

        // Assert — state after failure
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");
        Assert.IsNotNull(ct1.ErrorState);
        Assert.AreEqual(FailingHandler.Code, ct1.ErrorState.Code);
        Assert.AreEqual(FailingHandler.Message, ct1.ErrorState.Message);
        Assert.AreEqual(1, TestHandlers.Executions.Count);
    }

    [TestMethod]
    public async Task Unexpected_handler_exception_fails_the_activity_with_code_500()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<ThrowingHandler>("grpc-throwing"));
        await WaitForCatalogWorkers("grpc-throwing", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-throwing", "grpc-throwing"));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");
        Assert.AreEqual("500", ct1.ErrorState?.Code);
        StringAssert.Contains(ct1.ErrorState!.Message, ThrowingHandler.Message);
    }

    [TestMethod]
    public async Task Task_of_a_worker_that_disconnects_is_delivered_again_to_another_worker()
    {
        // Arrange — worker-a holds the task; worker-b, connected too, would complete it.
        var workerA = await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<GatedHandler>("grpc-handover"));
        await WaitForCatalogWorkers("grpc-handover", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-handover", "grpc-handover"));
        await TestHandlers.WaitForExecutions(1);
        await StartWorkerAsync("worker-b", s => s.AddExternalTaskHandler<EchoHandler>("grpc-handover"));
        await WaitForCatalogWorkers("grpc-handover", "worker-a", "worker-b");

        // Act — worker-a goes away before reporting.
        await workerA.StopAsync();

        // Assert — the engine abandoned worker-a's delivery and worker-b completed the task.
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 30_000);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual("worker-b", Variable(snapshot, "echoedBy"));
        Assert.AreEqual(1, TestHandlers.Cancellations.Count, "worker-a's run must be cancelled, not reported.");
        var executions = TestHandlers.Executions.ToList();
        Assert.HasCount(2, executions);
        Assert.AreEqual(executions[0].Context.ActivityInstanceId, executions[1].Context.ActivityInstanceId,
            "A redelivery keeps the activity instance id — the workers' idempotency key.");
        await WaitForCatalogWorkers("grpc-handover", "worker-b");
    }

    [TestMethod]
    public async Task Task_past_the_execution_timeout_is_cancelled_on_the_worker_and_delivered_again()
    {
        // Arrange — the gate holds the first run past the test silo's 5 s execution timeout.
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<GatedHandler>("grpc-slow"));
        await WaitForCatalogWorkers("grpc-slow", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-slow", "grpc-slow"));
        await TestHandlers.WaitForCancellations(1, TestExternalTaskExecutionTimeout * 4);
        await TestHandlers.WaitForExecutions(2, TestExternalTaskExecutionTimeout * 4);
        TestHandlers.Gate.TrySetResult();

        // Assert — the redelivery completed the task.
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "ct1");
    }

    [TestMethod]
    public async Task Redelivered_event_for_a_finished_activity_does_not_run_the_handler_again()
    {
        // Arrange — run the task to completion.
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>("grpc-echo"));
        await WaitForCatalogWorkers("grpc-echo", "worker-a");
        var workflow = SingleCustomTaskWorkflow("grpc-redelivery", "grpc-echo");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");

        // Act — the same event again, as an at-least-once stream may deliver it.
        CapturedLogEvents.Clear();
        await PublishCustomTaskEvent(instance.GetPrimaryKey(), workflow.WorkflowId, "grpc-echo",
            ct1.ActivityInstanceId, ct1.VariablesStateId);
        await CapturedLogEvents.WaitFor<ExternalTaskDispatcherGrain>(SkippedInactiveActivityEventId, 1);

        // Assert
        Assert.AreEqual(1, TestHandlers.Executions.Count,
            "A redelivered event for an already-completed activity must not run the handler again.");
        var after = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsTrue(after!.IsCompleted);
        Assert.IsFalse(after.IsFailed);
    }

    [TestMethod]
    public async Task Deterministic_activity_check_error_fails_the_activity_without_running_the_handler()
    {
        // Arrange — ct1 waits (a user task, so no custom-task delivery occupies the instance's
        // stream ahead of the event below).
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>("grpc-echo"));
        await WaitForCatalogWorkers("grpc-echo", "worker-a");
        var start = new StartEvent("start");
        var waiting = new UserTask("ct1", "ada", [], [], null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "grpc-bad-variables",
            Activities = [start, waiting, end],
            SequenceFlows = [new SequenceFlow("f1", start, waiting), new SequenceFlow("f2", waiting, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "ct1"));
        var ct1 = snapshot.ActiveActivities.Single(a => a.ActivityId == "ct1");

        // Act — an event for that activity whose variable scope does not exist: the activity
        // check throws a deterministic error that redelivery could never fix.
        await PublishCustomTaskEvent(instance.GetPrimaryKey(), workflow.WorkflowId, "grpc-echo",
            ct1.ActivityInstanceId, variablesId: Guid.NewGuid());

        // Assert — the activity fails visibly instead of staying Active; the handler never ran.
        var after = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        Assert.IsFalse(after.ActiveActivities.Any(a => a.ActivityId == "ct1"));
        Assert.IsEmpty(TestHandlers.Executions);
    }

    [TestMethod]
    public async Task Dispatcher_activation_is_freed_once_the_task_completed()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>("grpc-echo"));
        await WaitForCatalogWorkers("grpc-echo", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-dispatcher-freed", "grpc-echo"));

        // Assert — no dispatcher activation per task lingers for idle collection (#922).
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        var management = Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        var key = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ActivityInstanceId.ToString("D");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await management.GetDetailedGrainStatistics([typeof(ExternalTaskDispatcherGrain).FullName!]))
               .Any(s => s.GrainId.ToString().Contains(key)))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The dispatcher is still active after the task finished.");
            await Task.Delay(100);
        }
    }

    [TestMethod]
    public async Task Catalog_lists_a_worker_while_it_is_connected()
    {
        // Arrange
        var schema = new CustomTaskParameterSchema(
        [
            new CustomTaskParameterSpec("headers", "Headers", CustomTaskParameterType.Map,
                Required: false, Description: null, DefaultValue: null, ItemType: CustomTaskParameterType.String),
        ]);
        var worker = await StartWorkerAsync("worker-a",
            s => s.AddExternalTaskHandler<EchoHandler>("grpc-catalogued", displayName: "Catalogued", parameterSchema: schema));

        // Act / Assert — registered with its schema while connected...
        await WaitForCatalogWorkers("grpc-catalogued", "worker-a");
        var entry = await Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0).Get("grpc-catalogued");
        Assert.AreEqual("Catalogued", entry!.DisplayName);
        var headers = entry.ParameterSchema!.Parameters.Single();
        Assert.AreEqual(CustomTaskParameterType.Map, headers.Type);
        Assert.AreEqual(CustomTaskParameterType.String, headers.ItemType);

        // ...and gone once it disconnects.
        await worker.StopAsync();
        await WaitForCatalogWorkers("grpc-catalogued");
    }

    private Task<IHost> StartWorkerAsync(string workerName, Action<IServiceCollection> handlers, bool grpcWeb = false) =>
        _workers.StartAsync(workerName, handlers, grpcWeb);

    private Task WaitForCatalogWorkers(string taskType, params string[] workers) =>
        _workers.WaitForCatalogWorkers(taskType, workers);

    private static string? Variable(InstanceStateSnapshot snapshot, string name) =>
        snapshot.VariableStates.SelectMany(v => v.Variables).FirstOrDefault(kv => kv.Key == name).Value;

    private static WorkflowDefinition SingleCustomTaskWorkflow(string workflowId, string taskType)
    {
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", taskType);
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, ct1, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ct1),
                new SequenceFlow("f2", ct1, end)
            ]
        };
    }

    private Task PublishCustomTaskEvent(
        Guid workflowInstanceId, string workflowId, string taskType, Guid activityInstanceId, Guid variablesId)
        => Cluster.GrainFactory.GetGrain<IEventPublisher>(0).Publish(new ExecuteCustomTaskEvent(
            WorkflowInstanceId: workflowInstanceId,
            WorkflowId: workflowId,
            ProcessDefinitionId: null,
            ActivityInstanceId: activityInstanceId,
            ActivityId: "ct1",
            TaskType: taskType,
            Inputs: new ExpandoObject(),
            VariablesId: variablesId,
            BusinessId: null));

    /// <summary>What the test handlers saw, across every worker host of a test.</summary>
    private static class TestHandlers
    {
        public static ConcurrentQueue<(string Worker, ExternalTaskContext Context)> Executions { get; private set; } = new();
        public static ConcurrentQueue<ExternalTaskContext> Cancellations { get; private set; } = new();
        public static TaskCompletionSource Gate { get; private set; } = NewGate();

        public static void Reset()
        {
            Executions = new();
            Cancellations = new();
            Gate.TrySetResult();
            Gate = NewGate();
        }

        public static Task WaitForExecutions(int count, TimeSpan? timeout = null) =>
            WaitUntil(() => Executions.Count >= count, timeout, $"{count} handler executions");

        public static Task WaitForCancellations(int count, TimeSpan? timeout = null) =>
            WaitUntil(() => Cancellations.Count >= count, timeout, $"{count} handler cancellations");

        private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout, string what)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
            while (!condition())
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
                await Task.Delay(50);
            }
        }

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class EchoHandler(IOptions<ExternalTaskWorkerOptions> options) : ExternalTaskHandler
    {
        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            TestHandlers.Executions.Enqueue((options.Value.WorkerName, context));
            return Task.FromResult<IDictionary<string, object?>>(
                new Dictionary<string, object?> { ["echoedBy"] = options.Value.WorkerName });
        }
    }

    /// <summary>Holds every run until the gate opens; records a run that is cancelled first.</summary>
    private sealed class GatedHandler(IOptions<ExternalTaskWorkerOptions> options) : ExternalTaskHandler
    {
        public override async Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            TestHandlers.Executions.Enqueue((options.Value.WorkerName, context));
            try
            {
                await TestHandlers.Gate.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                TestHandlers.Cancellations.Enqueue(context);
                throw;
            }
            return new Dictionary<string, object?> { ["echoedBy"] = options.Value.WorkerName };
        }
    }

    private sealed class FailingHandler : ExternalTaskHandler
    {
        public const string Code = "E42";
        public const string Message = "the order was rejected";

        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            TestHandlers.Executions.Enqueue(("", context));
            throw new CustomTaskFailedActivityException(Code, Message);
        }
    }

    private sealed class ThrowingHandler : ExternalTaskHandler
    {
        public const string Message = "handler bug";

        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Message);
    }
}
