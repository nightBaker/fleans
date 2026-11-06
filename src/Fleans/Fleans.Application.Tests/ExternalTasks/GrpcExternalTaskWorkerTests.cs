extern alias sdk;

using Fleans.Application.CustomTasks;
using System.Collections.Concurrent;
using System.Dynamic;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Events;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.Sequences;
using Fleans.ExternalTasks.Sdk;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Protocol = sdk::Fleans.Application.ExternalTasks.Protocol;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// Custom tasks end to end over the worker protocol: the engine (TestCluster) publishes the task,
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
    public async Task Worker_announces_category_and_private_access()
    {
        // Arrange + Act
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>(
            "grpc-private", category: "Payments", visibility: ExternalTaskVisibility.Private, allowedProcessKeys: ["billing"]));
        await WaitForCatalogWorkers("grpc-private", "worker-a");

        // Assert
        var entry = await Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0).Get("grpc-private");
        Assert.AreEqual("Payments", entry!.Category);
        Assert.AreEqual(CustomTaskVisibility.Private, entry.Visibility);
        CollectionAssert.AreEqual(new[] { "billing" }, entry.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task Worker_without_access_metadata_is_public_and_uncategorized()
    {
        // Arrange + Act
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>("grpc-plain"));
        await WaitForCatalogWorkers("grpc-plain", "worker-a");

        // Assert
        var entry = await Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0).Get("grpc-plain");
        Assert.IsNull(entry!.Category);
        Assert.AreEqual(CustomTaskVisibility.Public, entry.Visibility);
    }

    [TestMethod]
    public void Handler_declaring_a_public_task_with_an_allow_list_is_rejected()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act + Assert
        Assert.ThrowsExactly<ArgumentException>(() =>
            services.AddExternalTaskHandler<EchoHandler>("grpc-bad", allowedProcessKeys: ["billing"]));
    }

    [TestMethod]
    public async Task Catalog_lists_a_worker_while_it_is_connected()
    {
        // Arrange
        var schema = new ExternalTaskParameterSchema(
        [
            new ExternalTaskParameter("headers", "Headers", ExternalTaskParameterType.Map,
                ItemType: ExternalTaskParameterType.String),
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

    // --- The behaviour plugin silos guaranteed, on gRPC workers (#1013) ---

    [TestMethod]
    public async Task Bpmn_error_code_from_FailTask_is_caught_by_the_matching_error_boundary()
    {
        // Arrange — the boundary catches the code the handler fails with.
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<FailingHandler>("grpc-boundary"));
        await WaitForCatalogWorkers("grpc-boundary", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(ErrorBoundaryWorkflow("grpc-boundary-caught", "grpc-boundary", FailingHandler.Code));

        // Assert — the recovery path ran, the instance did not fail, and the task records its error.
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "recoveredEnd");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
        Assert.AreEqual(FailingHandler.Code, snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState?.Code);
    }

    [TestMethod]
    public async Task Bpmn_error_code_from_FailTask_is_not_caught_by_a_boundary_for_another_code()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<FailingHandler>("grpc-boundary"));
        await WaitForCatalogWorkers("grpc-boundary", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(ErrorBoundaryWorkflow("grpc-boundary-missed", "grpc-boundary", "ANOTHER_CODE"));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "recoveredEnd");
        Assert.AreEqual(FailingHandler.Code, snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState?.Code);
    }

    [TestMethod]
    public async Task Redelivered_event_for_a_failed_activity_does_not_run_the_handler_again()
    {
        // Arrange — run the task to its failure.
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<FailingHandler>("grpc-failing"));
        await WaitForCatalogWorkers("grpc-failing", "worker-a");
        var workflow = SingleCustomTaskWorkflow("grpc-redelivery-failed", "grpc-failing");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        var ct1 = snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1");

        // Act
        CapturedLogEvents.Clear();
        await PublishCustomTaskEvent(instance.GetPrimaryKey(), workflow.WorkflowId, "grpc-failing",
            ct1.ActivityInstanceId, ct1.VariablesStateId);
        await CapturedLogEvents.WaitFor<ExternalTaskDispatcherGrain>(SkippedInactiveActivityEventId, 1);

        // Assert
        Assert.AreEqual(1, TestHandlers.Executions.Count);
        var after = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        Assert.IsTrue(after!.IsFailed);
    }

    [TestMethod]
    public async Task Dispatcher_activation_is_freed_once_the_task_failed()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<FailingHandler>("grpc-failing"));
        await WaitForCatalogWorkers("grpc-failing", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-dispatcher-freed-failed", "grpc-failing"));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        await WaitForDispatcherFreed(snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ActivityInstanceId);
    }

    [TestMethod]
    public async Task Tasks_of_many_instances_run_concurrently_across_workers_and_duplicates_are_skipped()
    {
        // Arrange — two workers, more tasks than one worker's concurrency limit.
        const int instanceCount = 40;
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<GatedHandler>("grpc-many"));
        await StartWorkerAsync("worker-b", s => s.AddExternalTaskHandler<GatedHandler>("grpc-many"));
        await WaitForCatalogWorkers("grpc-many", "worker-a", "worker-b");
        var workflow = SingleCustomTaskWorkflow("grpc-many", "grpc-many");
        var instances = Enumerable.Range(0, instanceCount)
            .Select(_ => Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid()))
            .ToList();
        CapturedLogEvents.Clear();
        await Task.WhenAll(instances.Select(i => i.StartWorkflow(workflow)));

        // Act 1 — every task is in flight at once, held by the gate.
        await TestHandlers.WaitForExecutions(instanceCount, TimeSpan.FromSeconds(20));

        // Act 2 — a duplicate event per task while it is in flight; they queue behind the delivery
        // on the same stream, and find the activity finished.
        foreach (var instance in instances)
        {
            var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivities.Any(a => a.ActivityId == "ct1"));
            var ct1 = snapshot.ActiveActivities.Single(a => a.ActivityId == "ct1");
            await PublishCustomTaskEvent(instance.GetPrimaryKey(), workflow.WorkflowId, "grpc-many",
                ct1.ActivityInstanceId, ct1.VariablesStateId);
        }
        TestHandlers.Gate.TrySetResult();
        foreach (var instance in instances)
            Assert.IsFalse((await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 30_000)).IsFailed);
        await CapturedLogEvents.WaitFor<ExternalTaskDispatcherGrain>(SkippedInactiveActivityEventId, instanceCount);

        // Assert — exactly once per task, on both workers.
        var executions = TestHandlers.Executions.ToList();
        Assert.AreEqual(instanceCount, executions.Count);
        Assert.AreEqual(instanceCount, executions.Select(e => e.Context.ActivityInstanceId).Distinct().Count());
        Assert.IsGreaterThan(1, executions.Select(e => e.Worker).Distinct().Count(), "Tasks must spread over the workers.");
    }

    [TestMethod]
    public async Task Parallel_custom_tasks_of_one_instance_run_concurrently_and_execute_once()
    {
        // Arrange — the dispatcher is keyed by activity instance (#815): branches must not queue.
        const int branchCount = 10;
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<GatedHandler>("grpc-branches"));
        await WaitForCatalogWorkers("grpc-branches", "worker-a");
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var join = new ParallelGateway("join", IsFork: false);
        var end = new EndEvent("end");
        var tasks = Enumerable.Range(0, branchCount).Select(i => new CustomTaskActivity($"ct{i}", "grpc-branches")).ToList();
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "grpc-parallel-branches",
            Activities = [start, fork, .. tasks, join, end],
            SequenceFlows =
            [
                new SequenceFlow("f-start", start, fork),
                .. tasks.Select(t => new SequenceFlow($"f-in-{t.ActivityId}", fork, t)),
                .. tasks.Select(t => new SequenceFlow($"f-out-{t.ActivityId}", t, join)),
                new SequenceFlow("f-end", join, end)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow);
        await TestHandlers.WaitForExecutions(branchCount, TimeSpan.FromSeconds(20));
        TestHandlers.Gate.TrySetResult();

        // Assert
        Assert.IsFalse((await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted)).IsFailed);
        var executions = TestHandlers.Executions.ToList();
        Assert.AreEqual(branchCount, executions.Count);
        Assert.AreEqual(branchCount, executions.Select(e => e.Context.ActivityInstanceId).Distinct().Count());
    }

    [TestMethod]
    public async Task Workers_with_different_task_types_each_get_only_their_own_tasks()
    {
        // Arrange — two hosts, same handler class, different types.
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<EchoHandler>("grpc-only-a"));
        await StartWorkerAsync("worker-b", s => s.AddExternalTaskHandler<EchoHandler>("grpc-only-b"));
        await WaitForCatalogWorkers("grpc-only-a", "worker-a");
        await WaitForCatalogWorkers("grpc-only-b", "worker-b");
        var instanceA = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var instanceB = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instanceA.StartWorkflow(SingleCustomTaskWorkflow("grpc-only-a", "grpc-only-a"));
        await instanceB.StartWorkflow(SingleCustomTaskWorkflow("grpc-only-b", "grpc-only-b"));

        // Assert
        Assert.AreEqual("worker-a", Variable(await WaitForCondition(instanceA.GetPrimaryKey(), s => s.IsCompleted), "echoedBy"));
        Assert.AreEqual("worker-b", Variable(await WaitForCondition(instanceB.GetPrimaryKey(), s => s.IsCompleted), "echoedBy"));
    }

    // --- Input and output mappings ---

    [TestMethod]
    public async Task Handler_receives_resolved_inputs_and_outputs_map_its_result()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<InputEchoHandler>("grpc-io"));
        await WaitForCatalogWorkers("grpc-io", "worker-a");
        var workflow = LinearCustomTask("grpc-io-mapped", new CustomTaskActivity("ct1", "grpc-io")
        {
            InputMappings = [new InputMapping("=count + 1", "amount")],
            OutputMappings = [new OutputMapping("=received.amount", "charged")]
        });
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow, variables: Vars(("count", 41L)));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.AreEqual(42L, Convert.ToInt64(TestHandlers.LastInputs!["amount"]));
        Assert.AreEqual("42", Variable(snapshot, "charged"));
        Assert.IsNull(Variable(snapshot, "status"), "Unmapped result fields are dropped.");
        Assert.IsNull(Variable(snapshot, "amount"));
    }

    // --- Declared variables ---

    [TestMethod]
    public async Task Handler_that_declares_nothing_receives_no_variables_but_still_its_inputs()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<VariablesHandler>("grpc-vars"));
        await WaitForCatalogWorkers("grpc-vars", "worker-a");
        var workflow = LinearCustomTask("grpc-vars-none", new CustomTaskActivity("ct1", "grpc-vars")
        {
            InputMappings = [new InputMapping("=count + 1", "amount")],
        });
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(workflow, variables: Vars(("count", 41L)));

        // Assert
        await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        var run = TestHandlers.VariableRuns.Single();
        Assert.IsEmpty(run.Names);
        Assert.AreEqual(42L, Convert.ToInt64(TestHandlers.LastInputs!["amount"]));
    }

    [TestMethod]
    public async Task Handler_that_declares_all_receives_every_variable()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<VariablesHandler>("grpc-vars", variables: ExternalTaskVariables.All));
        await WaitForCatalogWorkers("grpc-vars", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-vars-all", "grpc-vars"), variables: Vars(("a", 1L), ("b", 2L)));

        // Assert
        await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, TestHandlers.VariableRuns.Single().Names);
    }

    [TestMethod]
    public async Task Handler_that_declares_names_receives_those_and_a_missing_name_is_left_out()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<VariablesHandler>(
            "grpc-vars", variables: ExternalTaskVariables.Names("a", "missing")));
        await WaitForCatalogWorkers("grpc-vars", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-vars-names", "grpc-vars"), variables: Vars(("a", 1L), ("b", 2L)));

        // Assert
        await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        CollectionAssert.AreEquivalent(new[] { "a" }, TestHandlers.VariableRuns.Single().Names);
    }

    [TestMethod]
    public async Task Handler_on_a_wildcard_receives_the_variables_it_declared()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<VariablesHandler>(
            "vars-scheme:*", variables: ExternalTaskVariables.Names("a")));
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act — a wildcard is not in the catalog, so nothing to wait for: the stream redelivers until it joined.
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-vars-wildcard", "vars-scheme:x"), variables: Vars(("a", 1L), ("b", 2L)));

        // Assert
        await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 30_000);
        CollectionAssert.AreEquivalent(new[] { "a" }, TestHandlers.VariableRuns.Single().Names);
    }

    [TestMethod]
    public async Task Workers_of_one_type_each_receive_only_what_they_declared()
    {
        // Arrange — as during a rolling upgrade. The dispatcher picks at random, so run enough tasks.
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<VariablesHandler>("grpc-vars-mixed", variables: ExternalTaskVariables.Names("a")));
        await StartWorkerAsync("worker-b", s => s.AddExternalTaskHandler<VariablesHandler>("grpc-vars-mixed"));
        await WaitForCatalogWorkers("grpc-vars-mixed", "worker-a", "worker-b");
        var workflow = SingleCustomTaskWorkflow("grpc-vars-mixed", "grpc-vars-mixed");
        var instances = Enumerable.Range(0, 10).Select(_ => Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid())).ToList();

        // Act
        foreach (var instance in instances)
            await instance.StartWorkflow(workflow, variables: Vars(("a", 1L), ("b", 2L)));

        // Assert
        foreach (var instance in instances)
            await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 30_000);
        foreach (var run in TestHandlers.VariableRuns)
            CollectionAssert.AreEqual(run.Worker == "worker-a" ? new[] { "a" } : Array.Empty<string>(), run.Names, run.Worker);
    }

    [TestMethod]
    public async Task Instance_returns_only_the_selection_so_the_rest_never_leaves_the_grain()
    {
        // Arrange — a user task keeps an activity active and its instance id at hand.
        var start = new StartEvent("start");
        var waiting = new UserTask("ct1", "ada", [], [], null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "grpc-vars-grain",
            Activities = [start, waiting, end],
            SequenceFlows = [new SequenceFlow("f1", start, waiting), new SequenceFlow("f2", waiting, end)]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: Vars(("a", 1L), ("b", 2L)));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivities.Any(a => a.ActivityId == "ct1"));
        var ct1 = snapshot.ActiveActivities.Single(a => a.ActivityId == "ct1");

        // Act
        var none = await instance.GetVariablesIfActive(ct1.ActivityInstanceId, ct1.VariablesStateId, VariableSelection.None);
        var named = await instance.GetVariablesIfActive(ct1.ActivityInstanceId, ct1.VariablesStateId, new VariableSelection(false, ["b"]));
        var all = await instance.GetVariablesIfActive(ct1.ActivityInstanceId, ct1.VariablesStateId, VariableSelection.All);

        // Assert
        Assert.IsEmpty((IDictionary<string, object?>)none!);
        CollectionAssert.AreEqual(new[] { "b" }, ((IDictionary<string, object?>)named!).Keys.ToArray());
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, ((IDictionary<string, object?>)all!).Keys.ToArray());
    }

    [TestMethod]
    [DataRow(true, "a", DisplayName = "all_variables with variable_names")]
    [DataRow(false, " ", DisplayName = "a blank variable name")]
    public async Task Subscription_with_a_contradictory_variable_declaration_is_refused(bool allVariables, string name)
    {
        // Arrange
        using var channel = GrpcChannel.ForAddress("http://engine", new GrpcChannelOptions { HttpHandler = await _workers.GetEngineHandlerAsync() });
        var client = new Protocol.ExternalTaskWorker.ExternalTaskWorkerClient(channel);
        var request = new Protocol.SubscribeRequest
        {
            WorkerName = "worker-a",
            TaskTypes = { new Protocol.TaskTypeRegistration { TaskType = "grpc-vars-refused", AllVariables = allVariables, VariableNames = { name } } },
        };

        // Act
        using var call = client.Subscribe(request);
        var refused = await Assert.ThrowsExactlyAsync<RpcException>(() => call.ResponseStream.MoveNext(CancellationToken.None));

        // Assert
        Assert.AreEqual(StatusCode.InvalidArgument, refused.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void A_blank_variable_name_is_rejected_at_registration(string name)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ExternalTaskVariables.Names("a", name));
    }

    [TestMethod]
    public void Union_of_selections_covers_each_of_them()
    {
        Assert.IsTrue(VariableSelection.Union([new VariableSelection(false, ["a"]), VariableSelection.All]).IncludeAll);
        Assert.IsEmpty(VariableSelection.Union([]).Names);
        var names = VariableSelection.Union([new VariableSelection(false, ["a"]), VariableSelection.None, new VariableSelection(false, ["a", "b"])]);
        Assert.IsFalse(names.IncludeAll);
        CollectionAssert.AreEquivalent(new[] { "a", "b" }, names.Names);
    }

    [TestMethod]
    public async Task Without_outputs_the_whole_result_is_merged()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<InputEchoHandler>("grpc-io"));
        await WaitForCatalogWorkers("grpc-io", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-io-merged", "grpc-io"));

        // Assert
        Assert.AreEqual("done", Variable(await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted), "status"));
    }

    [TestMethod]
    public async Task Input_that_does_not_evaluate_fails_without_calling_the_handler()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<InputEchoHandler>("grpc-io"));
        await WaitForCatalogWorkers("grpc-io", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(LinearCustomTask("grpc-io-input-error", new CustomTaskActivity("ct1", "grpc-io")
        {
            InputMappings = [new InputMapping("=order.Total * 2", "amount")] // no 'order'
        }));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        StringAssert.Contains(snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState!.Message, "'amount'");
        Assert.IsNull(TestHandlers.LastInputs);
    }

    [TestMethod]
    public async Task Output_that_does_not_evaluate_fails_the_task()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<InputEchoHandler>("grpc-io"));
        await WaitForCatalogWorkers("grpc-io", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act
        await instance.StartWorkflow(LinearCustomTask("grpc-io-output-error", new CustomTaskActivity("ct1", "grpc-io")
        {
            OutputMappings = [new OutputMapping("=status.Missing * 2", "x")]
        }));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        StringAssert.Contains(snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState!.Message,
            "Output mapping '=status.Missing * 2' -> 'x' on activity 'ct1'");
        Assert.IsNull(Variable(snapshot, "status"), "A failed task merges nothing.");
    }

    // --- Deferred outcome and scheme wildcards (the Camunda bridge's shape, #1013) ---

    [TestMethod]
    public async Task Deferred_task_stays_active_past_the_execution_timeout_until_reported_out_of_band()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<DeferringHandler>("grpc-defer"));
        await WaitForCatalogWorkers("grpc-defer", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act — the handler defers; wait past the 5 s execution timeout, which would redeliver a waiting task.
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-deferred", "grpc-defer"));
        await TestHandlers.WaitForExecutions(1);
        await Task.Delay(TestExternalTaskExecutionTimeout + TimeSpan.FromSeconds(2));

        // Assert — still active, not run again, and the dispatcher activation is gone.
        var waiting = await QueryService.GetStateSnapshot(instance.GetPrimaryKey());
        var ct1 = waiting!.ActiveActivities.Single(a => a.ActivityId == "ct1");
        Assert.AreEqual(1, TestHandlers.Executions.Count, "A deferred task must not be delivered again.");
        await WaitForDispatcherFreed(ct1.ActivityInstanceId);
        var commands = GetSiloService<IWorkflowCommandService>();
        Assert.IsNotNull(await commands.GetActiveActivityVariables(instance.GetPrimaryKey(), ct1.ActivityInstanceId));

        // Act — the out-of-band report (POST /Execution/complete-activity).
        await commands.CompleteActivity(
            instance.GetPrimaryKey(), "ct1", ct1.ActivityInstanceId, Vars(("paymentRef", "ch_1")));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual("ch_1", Variable(snapshot, "paymentRef"));
        Assert.IsNull(await commands.GetActiveActivityVariables(instance.GetPrimaryKey(), ct1.ActivityInstanceId));
    }

    [TestMethod]
    public async Task Deferred_task_reported_as_failed_out_of_band_records_its_error_code()
    {
        // Arrange
        await StartWorkerAsync("worker-a", s => s.AddExternalTaskHandler<DeferringHandler>("grpc-defer"));
        await WaitForCatalogWorkers("grpc-defer", "worker-a");
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(SingleCustomTaskWorkflow("grpc-deferred-failed", "grpc-defer"));
        await TestHandlers.WaitForExecutions(1);
        var waiting = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivities.Any(a => a.ActivityId == "ct1"));
        var activityInstanceId = waiting.ActiveActivities.Single(a => a.ActivityId == "ct1").ActivityInstanceId;

        // Act
        await GetSiloService<IWorkflowCommandService>().FailActivity(
            instance.GetPrimaryKey(), "ct1", activityInstanceId, "DECLINED", "card declined", null);

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);
        Assert.AreEqual("DECLINED", snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState?.Code);
    }

    [TestMethod]
    public async Task Wildcard_worker_gets_every_type_of_its_scheme_unless_a_worker_claims_it_exactly()
    {
        // Arrange
        await StartWorkerAsync("wildcard", s => s.AddExternalTaskHandler<EchoHandler>("scheme:*"));
        await StartWorkerAsync("exact", s => s.AddExternalTaskHandler<EchoHandler>("scheme:exact"));
        await WaitForCatalogWorkers("scheme:exact", "exact");
        var viaWildcard = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        var viaExact = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());

        // Act — the wildcard's subscription may still be registering: the stream redelivers until then.
        await viaWildcard.StartWorkflow(SingleCustomTaskWorkflow("grpc-wildcard", "scheme:charge-card"));
        await viaExact.StartWorkflow(SingleCustomTaskWorkflow("grpc-wildcard-exact", "scheme:exact"));

        // Assert
        var snapshot = await WaitForCondition(viaWildcard.GetPrimaryKey(), s => s.IsCompleted, timeoutMs: 30_000);
        Assert.AreEqual("wildcard", Variable(snapshot, "echoedBy"));
        Assert.AreEqual("exact", Variable(await WaitForCondition(viaExact.GetPrimaryKey(), s => s.IsCompleted), "echoedBy"));
        Assert.IsNull(await Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0).Get("scheme:*"),
            "A wildcard is nothing an author can pick in the editor.");
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow("scheme*")]
    [DataRow("sch*me:x")]
    public void Handler_with_a_malformed_wildcard_is_rejected(string taskType)
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ServiceCollection().AddExternalTaskHandler<EchoHandler>(taskType));
    }

    private async Task WaitForDispatcherFreed(Guid activityInstanceId)
    {
        var management = Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        var key = activityInstanceId.ToString("D");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await management.GetDetailedGrainStatistics([typeof(ExternalTaskDispatcherGrain).FullName!]))
               .Any(s => s.GrainId.ToString().Contains(key)))
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The dispatcher is still active after the task finished.");
            await Task.Delay(100);
        }
    }

    private static ExpandoObject Vars(params (string Name, object? Value)[] values)
    {
        var vars = new ExpandoObject();
        foreach (var (name, value) in values)
            ((IDictionary<string, object?>)vars)[name] = value;
        return vars;
    }

    private static WorkflowDefinition LinearCustomTask(string workflowId, CustomTaskActivity task)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)]
        };
    }

    private static WorkflowDefinition ErrorBoundaryWorkflow(string workflowId, string taskType, string caughtCode)
    {
        var start = new StartEvent("start");
        var ct1 = new CustomTaskActivity("ct1", taskType);
        var boundary = new BoundaryErrorEvent("boundary", "ct1", caughtCode);
        var end = new EndEvent("end");
        var recoveredEnd = new EndEvent("recoveredEnd");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, ct1, boundary, end, recoveredEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, ct1),
                new SequenceFlow("f2", ct1, end),
                new SequenceFlow("f3", boundary, recoveredEnd)
            ]
        };
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
        public static ConcurrentQueue<(string Worker, string[] Names)> VariableRuns { get; private set; } = new();
        public static volatile IDictionary<string, object?>? LastInputs;

        public static void Reset()
        {
            Executions = new();
            VariableRuns = new();
            Cancellations = new();
            LastInputs = null;
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

    /// <summary>Records its inputs; the result carries them (<c>received</c>) and a <c>status</c>.</summary>
    /// <summary>Records the names of the variables it received, and its inputs. Sets no variables itself.</summary>
    private sealed class VariablesHandler(IOptions<ExternalTaskWorkerOptions> options) : ExternalTaskHandler
    {
        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            TestHandlers.LastInputs = resolvedInputs;
            TestHandlers.VariableRuns.Enqueue((options.Value.WorkerName, ((IDictionary<string, object?>)variables).Keys.ToArray()));
            return Task.FromResult<IDictionary<string, object?>>(new Dictionary<string, object?>());
        }
    }

    private sealed class InputEchoHandler : ExternalTaskHandler
    {
        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            TestHandlers.LastInputs = resolvedInputs;
            return Task.FromResult<IDictionary<string, object?>>(
                new Dictionary<string, object?> { ["status"] = "done", ["received"] = resolvedInputs });
        }
    }

    private sealed class DeferringHandler(IOptions<ExternalTaskWorkerOptions> options) : ExternalTaskHandler
    {
        public override Task<IDictionary<string, object?>> ExecuteAsync(
            IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
            ExternalTaskContext context, CancellationToken cancellationToken)
        {
            TestHandlers.Executions.Enqueue((options.Value.WorkerName, context));
            return Task.FromResult(Deferred);
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
            throw new ExternalTaskFailedException(Code, Message);
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
