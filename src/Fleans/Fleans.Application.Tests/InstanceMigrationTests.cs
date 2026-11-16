using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Application.InstanceMigration;
using Fleans.Application.QueryModels;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Events;
using Fleans.Domain.InstanceMigration;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Tests;

/// <summary>
/// Instance migration through the grain (#1069): an instance started on v1 moves to v2, keeps its
/// id and waits, and finishes on v2 — completed or failed.
/// </summary>
[TestClass]
public class InstanceMigrationTests : WorkflowTestBase
{
    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    private Task<ProcessDefinitionSummary> Deploy(WorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId).DeployVersion(workflow, "<xml/>");

    private static WorkflowDefinition Linear(string workflowId, Activity element, params Activity[] extra)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, element, end, .. extra],
            SequenceFlows = [new SequenceFlow("f1", start, element), new SequenceFlow("f2", element, end)],
            Messages = [new MessageDefinition("msg", "MigrationPaid", "= orderId")],
        };
    }

    private static UserTask Task(string id) => new(id, null, [], [], null);

    private static ExpandoObject Order(string orderId)
    {
        var variables = new ExpandoObject();
        ((IDictionary<string, object?>)variables)["orderId"] = orderId;
        return variables;
    }

    private async Task<(Guid InstanceId, string V1, string V2)> StartOnV1(WorkflowDefinition v1, WorkflowDefinition v2, ExpandoObject? variables = null)
    {
        var first = await Deploy(v1);
        var second = await Deploy(v2);
        var instanceId = await CommandService.StartWorkflowByProcessDefinitionId(first.ProcessDefinitionId, initialVariables: variables);
        return (instanceId, first.ProcessDefinitionId, second.ProcessDefinitionId);
    }

    private IWorkflowInstanceGrain Instance(Guid id) => Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(id);

    private async Task<ActivityInstanceSnapshot> Active(Guid instanceId, string activityId)
    {
        var snapshot = await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == activityId));
        return snapshot.ActiveActivities.Single(a => a.ActivityId == activityId);
    }

    private async Task CompleteTask(Guid instanceId, Guid activityInstanceId)
    {
        await Instance(instanceId).ClaimUserTask(activityInstanceId, "ann", []);
        await Instance(instanceId).CompleteUserTask(activityInstanceId, "ann", new ExpandoObject());
    }

    // ── User task renamed ────────────────────────────────────────────────────

    [TestMethod]
    public async Task RenamedUserTask_CompletedAfterMigration_InstanceCompletesOnV2()
    {
        // Arrange
        var (id, _, v2) = await StartOnV1(Linear("mig-ut-complete", Task("review")), Linear("mig-ut-complete", Task("approve")));
        var task = await Active(id, "review");

        // Act
        var result = await Instance(id).Migrate("m1", new MigrationPlan(v2, [new ActivityMapping("review", "approve")]), dryRun: false);
        await CompleteTask(id, task.ActivityInstanceId);

        // Assert
        Assert.AreEqual(InstanceMigrationOutcome.Migrated, result.Outcome);
        var snapshot = await QueryService.GetStateSnapshot(id);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(v2, snapshot.ProcessDefinitionId);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "approve" && a.ActivityInstanceId == task.ActivityInstanceId),
            "the waiting element kept its instance id under its new element id");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "start"), "history keeps what ran on v1");
    }

    [TestMethod]
    public async Task RenamedUserTask_FailedAfterMigration_InstanceFailsOnV2()
    {
        // Arrange
        var (id, _, v2) = await StartOnV1(Linear("mig-ut-fail", Task("review")), Linear("mig-ut-fail", Task("approve")));
        var task = await Active(id, "review");
        await Instance(id).Migrate("m1", new MigrationPlan(v2, [new ActivityMapping("review", "approve")]), dryRun: false);

        // Act
        await Instance(id).FailUserTask(task.ActivityInstanceId, "rejected", "no budget");

        // Assert
        var snapshot = await QueryService.GetStateSnapshot(id);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsFailed);
        Assert.AreEqual(v2, snapshot.ProcessDefinitionId);
        var failed = snapshot.CompletedActivities.Single(a => a.ActivityInstanceId == task.ActivityInstanceId);
        Assert.AreEqual("approve", failed.ActivityId);
        Assert.IsNotNull(failed.ErrorState);
    }

    [TestMethod]
    public async Task RenamedUserTask_ReadModelRowFollows()
    {
        var (id, _, v2) = await StartOnV1(Linear("mig-ut-row", Task("review")), Linear("mig-ut-row", Task("approve")));
        var task = await Active(id, "review");

        await Instance(id).Migrate("m1", new MigrationPlan(v2, [new ActivityMapping("review", "approve")]), dryRun: false);

        var row = await QueryService.GetUserTask(task.ActivityInstanceId);
        Assert.IsNotNull(row);
        Assert.AreEqual("approve", row.ActivityId);
    }

    // ── Dry run, rejection, idempotency, replay ──────────────────────────────

    [TestMethod]
    public async Task DryRun_ReportsWithoutChangingAnything()
    {
        var (id, v1, v2) = await StartOnV1(Linear("mig-dry", Task("review")), Linear("mig-dry", Task("approve")));
        await Active(id, "review");

        var unmapped = await Instance(id).Migrate("m1", new MigrationPlan(v2, []), dryRun: true);
        var mapped = await Instance(id).Migrate("m2", new MigrationPlan(v2, [new ActivityMapping("review", "approve")]), dryRun: true);

        Assert.AreEqual(InstanceMigrationOutcome.Rejected, unmapped.Outcome);
        Assert.AreEqual(MigrationRejectionCodes.UnmappedActivity, unmapped.Rejections.Single().Code);
        Assert.AreEqual(InstanceMigrationOutcome.Migratable, mapped.Outcome);
        Assert.AreEqual(v1, (await QueryService.GetStateSnapshot(id))!.ProcessDefinitionId);
    }

    [TestMethod]
    public async Task Rejected_ReturnsTheCodes_InstanceKeepsRunningOnV1()
    {
        // Arrange
        var (id, v1, v2) = await StartOnV1(Linear("mig-reject", Task("review")), Linear("mig-reject", new TaskActivity("review")));
        var task = await Active(id, "review");

        // Act
        var result = await Instance(id).Migrate("m1", new MigrationPlan(v2, []), dryRun: false);
        await CompleteTask(id, task.ActivityInstanceId);

        // Assert
        Assert.AreEqual(InstanceMigrationOutcome.Rejected, result.Outcome);
        Assert.AreEqual(MigrationRejectionCodes.TypeChanged, result.Rejections.Single().Code);
        var snapshot = await QueryService.GetStateSnapshot(id);
        Assert.IsTrue(snapshot!.IsCompleted);
        Assert.AreEqual(v1, snapshot.ProcessDefinitionId);
    }

    [TestMethod]
    public async Task SameMigrationTwice_OneEvent_SecondCallIsANoOp()
    {
        // Arrange
        var (id, _, v2) = await StartOnV1(Linear("mig-idem", Task("review")), Linear("mig-idem", Task("approve")));
        await Active(id, "review");
        var plan = new MigrationPlan(v2, [new ActivityMapping("review", "approve")]);

        // Act
        var first = await Instance(id).Migrate("m1", plan, dryRun: false);
        var again = await Instance(id).Migrate("m1", plan, dryRun: false);
        var otherId = await Instance(id).Migrate("m2", plan, dryRun: false);

        // Assert
        Assert.AreEqual(InstanceMigrationOutcome.Migrated, first.Outcome);
        Assert.AreEqual(InstanceMigrationOutcome.AlreadyMigrated, again.Outcome);
        Assert.AreEqual(InstanceMigrationOutcome.AlreadyMigrated, otherId.Outcome, "same target and mapping: nothing to do");
        var journal = await GetSiloService<IEventStore>().ReadEventsAsync(id, 0);
        Assert.HasCount(1, journal.OfType<InstanceMigrated>().ToList());
    }

    [TestMethod]
    public async Task Migrated_AfterReactivation_ReplaysToV2AndCompletes()
    {
        // Arrange
        var (id, _, v2) = await StartOnV1(Linear("mig-replay", Task("review")), Linear("mig-replay", Task("approve")));
        var task = await Active(id, "review");
        await Instance(id).Migrate("m1", new MigrationPlan(v2, [new ActivityMapping("review", "approve")]), dryRun: false);

        // Act
        await ForceAllGrainDeactivation(id);
        var reactivated = await QueryService.GetStateSnapshot(id);
        await CompleteTask(id, task.ActivityInstanceId);

        // Assert
        Assert.AreEqual(v2, reactivated!.ProcessDefinitionId);
        Assert.AreEqual("approve", reactivated.ActiveActivities.Single().ActivityId);
        var snapshot = await QueryService.GetStateSnapshot(id);
        Assert.IsTrue(snapshot!.IsCompleted);
    }

    // ── Message, timer, join, call activity ──────────────────────────────────

    [TestMethod]
    public async Task RenamedMessageCatch_MessageAfterMigration_CompletesOnV2()
    {
        // Arrange
        var (id, _, v2) = await StartOnV1(
            Linear("mig-msg", new MessageIntermediateCatchEvent("paid", "msg")),
            Linear("mig-msg", new MessageIntermediateCatchEvent("paymentReceived", "msg")),
            Order("mig-msg-1"));
        await Active(id, "paid");

        // Act
        await Instance(id).Migrate("m1", new MigrationPlan(v2, [new ActivityMapping("paid", "paymentReceived")]), dryRun: false);
        var delivered = await DeliverMessageWhenSubscribed("MigrationPaid", "mig-msg-1");

        // Assert
        Assert.IsTrue(delivered);
        var snapshot = await WaitForCondition(id, s => s.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "paymentReceived"));
    }

    [TestMethod]
    public async Task BoundaryTimerAddedInV2_FiresOnTheMigratedTask()
    {
        // Arrange
        var v2Definition = Linear("mig-timer", Task("review"),
            new BoundaryTimerEvent("escalate", "review", new TimerDefinition(TimerType.Duration, "PT1H")));
        var (id, _, v2) = await StartOnV1(Linear("mig-timer", Task("review")), v2Definition);
        var task = await Active(id, "review");
        await Instance(id).Migrate("m1", new MigrationPlan(v2, []), dryRun: false);

        // Act: the reminder the migration registered fires.
        await Instance(id).HandleTimerFired("escalate", task.ActivityInstanceId);

        // Assert: the interrupting boundary cancelled the task and its path (no outgoing flow) ended.
        var snapshot = await WaitForCondition(id, s => s.ActiveActivities.All(a => a.ActivityId != "review"));
        var review = snapshot.CompletedActivities.Single(a => a.ActivityInstanceId == task.ActivityInstanceId);
        Assert.IsTrue(review.IsCancelled);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "escalate"));
    }

    [TestMethod]
    public async Task ParallelJoinWithAWaitingToken_MigratesAndJoins()
    {
        // Arrange: a reached the join; b waits. v2 only adds a boundary timer to b.
        WorkflowDefinition Definition(bool withTimer)
        {
            var start = new StartEvent("start");
            var fork = new ParallelGateway("fork", IsFork: true);
            var a = new TaskActivity("a");
            var b = Task("b");
            var join = new ParallelGateway("join", IsFork: false);
            var end = new EndEvent("end");
            List<Activity> activities = [start, fork, a, b, join, end];
            if (withTimer)
                activities.Add(new BoundaryTimerEvent("bTimeout", "b", new TimerDefinition(TimerType.Duration, "P1D")));
            return new WorkflowDefinition
            {
                WorkflowId = "mig-join",
                Activities = activities,
                SequenceFlows =
                [
                    new("f1", start, fork), new("f2", fork, a), new("f3", fork, b),
                    new("f4", a, join), new("f5", b, join), new("f6", join, end),
                ],
            };
        }
        var (id, _, v2) = await StartOnV1(Definition(false), Definition(true));
        var a = await Active(id, "a");
        await Instance(id).CompleteActivity("a", a.ActivityInstanceId, new ExpandoObject());
        var b = await Active(id, "b");

        // Act
        var result = await Instance(id).Migrate("m1", new MigrationPlan(v2, []), dryRun: false);
        await CompleteTask(id, b.ActivityInstanceId);

        // Assert
        Assert.AreEqual(InstanceMigrationOutcome.Migrated, result.Outcome, string.Join(",", result.Rejections.Select(r => r.Code)));
        var snapshot = await WaitForCondition(id, s => s.IsCompleted);
        Assert.IsFalse(snapshot.IsFailed);
    }

    [TestMethod]
    public async Task RenamedCallActivity_ChildCompletesBackIntoTheNewElement()
    {
        // Arrange
        await Deploy(Linear("mig-child", Task("pay")));
        var (id, _, v2) = await StartOnV1(
            Linear("mig-parent", new CallActivity("call", "mig-child", [], [])),
            Linear("mig-parent", new CallActivity("bill", "mig-child", [], [])));
        var call = await Active(id, "call");
        var childId = call.ChildWorkflowInstanceId!.Value;
        var pay = await Active(childId, "pay");

        // Act
        await Instance(id).Migrate("m1", new MigrationPlan(v2, [new ActivityMapping("call", "bill")]), dryRun: false);
        await CompleteTask(childId, pay.ActivityInstanceId);

        // Assert
        var snapshot = await WaitForCondition(id, s => s.IsCompleted);
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "bill" && a.ActivityInstanceId == call.ActivityInstanceId));
    }

    // ── Batch service ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Batch_AllRunningInstancesOfAVersion_RetriedRequestIsANoOp()
    {
        // Arrange
        var first = await Deploy(Linear("mig-batch", Task("review")));
        var second = await Deploy(Linear("mig-batch", Task("approve")));
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
            ids.Add(await CommandService.StartWorkflowByProcessDefinitionId(first.ProcessDefinitionId));
        await WaitForProjection([.. ids]);
        var service = GetSiloService<IInstanceMigrationService>();
        var request = new InstanceMigrationRequest(null, first.ProcessDefinitionId, second.ProcessDefinitionId,
            [new ActivityMapping("review", "approve")], RequestId: "req-1");

        // Act
        var dryRun = await service.MigrateAsync(request with { DryRun = true });
        var migrated = await service.MigrateAsync(request);
        var retried = await service.MigrateAsync(request with { InstanceIds = ids, SourceProcessDefinitionId = null });

        // Assert
        Assert.HasCount(3, dryRun.Results);
        Assert.IsTrue(dryRun.Results.All(r => r.Outcome == InstanceMigrationOutcome.Migratable));
        Assert.IsTrue(migrated.Results.All(r => r.Outcome == InstanceMigrationOutcome.Migrated));
        CollectionAssert.AreEquivalent(ids, migrated.Results.Select(r => r.InstanceId).ToList());
        Assert.IsNull(migrated.ContinuationToken);
        Assert.IsTrue(retried.Results.All(r => r.Outcome == InstanceMigrationOutcome.AlreadyMigrated));
    }

    [TestMethod]
    public async Task Batch_UnknownInstance_ReportedAsFailed()
    {
        var target = await Deploy(Linear("mig-batch-unknown", Task("review")));
        var service = GetSiloService<IInstanceMigrationService>();

        var result = await service.MigrateAsync(new InstanceMigrationRequest([Guid.NewGuid()], null, target.ProcessDefinitionId));

        Assert.AreEqual(InstanceMigrationOutcome.Failed, result.Results.Single().Outcome);
    }
}
