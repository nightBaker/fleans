using System.Dynamic;
using Fleans.Domain.Activities;
using Fleans.Domain.Aggregates;
using Fleans.Domain.Effects;
using Fleans.Domain.Events;
using Fleans.Domain.InstanceMigration;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

/// <summary>
/// Instance migration (#1069): the validator (one test per rejection code), the
/// <see cref="InstanceMigrated"/> transition and its replay, and the rewiring of what waiting
/// elements subscribed to.
/// </summary>
[TestClass]
public class InstanceMigrationTests
{
    private const string V1 = "order:1:20261101T000000.0000000Z";
    private const string V2 = "order:2:20261102T000000.0000000Z";

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static WorkflowDefinition Process(string processDefinitionId, List<Activity> activities, List<SequenceFlow>? flows = null,
        List<MessageDefinition>? messages = null, List<SignalDefinition>? signals = null, string workflowId = "order")
        => new()
        {
            WorkflowId = workflowId,
            ProcessDefinitionId = processDefinitionId,
            Activities = activities,
            SequenceFlows = flows ?? [],
            Messages = messages ?? [],
            Signals = signals ?? [],
        };

    /// <summary>start → element → end, in its own definition.</summary>
    private static WorkflowDefinition Linear(string processDefinitionId, Activity element, params Activity[] extra)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return Process(processDefinitionId, [start, element, end, .. extra],
            [new SequenceFlow("f1", start, element), new SequenceFlow("f2", element, end)],
            messages: [new MessageDefinition("msg", "OrderPaid", "= orderId"), new MessageDefinition("msg2", "OrderShipped", "= orderId")],
            signals: [new SignalDefinition("sig", "Alarm"), new SignalDefinition("sig2", "Fire")]);
    }

    private sealed class Running
    {
        public required WorkflowInstanceState State { get; init; }
        public required WorkflowExecution Execution { get; init; }
        public required IWorkflowDefinition Source { get; init; }
    }

    /// <summary>A started instance of <paramref name="source"/> whose start event finished, with variable orderId.</summary>
    private static Running Start(IWorkflowDefinition source)
    {
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, source);
        var variables = new ExpandoObject();
        ((IDictionary<string, object?>)variables)["orderId"] = "o-1";
        execution.CreateAndStart(Guid.NewGuid(), variables: variables);
        state.CompleteEntry(state.Entries.Single().ActivityInstanceId);
        execution.ClearUncommittedEvents();
        return new Running { State = state, Execution = execution, Source = source };
    }

    /// <summary>Adds an executing (waiting) entry of <paramref name="activityId"/>.</summary>
    private static ActivityInstanceEntry Wait(Running run, string activityId, Guid? scopeId = null, int? multiInstanceIndex = null,
        DateTimeOffset? since = null)
    {
        var element = run.Source.GetActivityAcrossScopes(activityId);
        var type = element is MultiInstanceActivity mi && multiInstanceIndex is not null ? mi.InnerActivity.GetType().Name : element.GetType().Name;
        var entry = multiInstanceIndex is { } index
            ? new ActivityInstanceEntry(Guid.NewGuid(), activityId, run.State.Id, scopeId, index)
            : new ActivityInstanceEntry(Guid.NewGuid(), activityId, run.State.Id, scopeId);
        var at = since ?? DateTimeOffset.UtcNow;
        entry.SetActivityType(type);
        entry.SetVariablesId(run.State.GetRootVariablesId());
        entry.SetCreatedAt(at);
        run.State.AddEntries([entry]);
        run.State.ExecuteEntry(entry.ActivityInstanceId, at);
        return entry;
    }

    private static MigrationPlan Plan(bool updateEventTriggers = false, params ActivityMapping[] mappings)
        => new(V2, mappings.ToList(), updateEventTriggers);

    private static List<MigrationRejection> Validate(Running run, IWorkflowDefinition target, MigrationPlan? plan = null)
        => run.Execution.ValidateMigration(plan ?? Plan(), target);

    private static void AssertRejected(List<MigrationRejection> rejections, string code, string? activityId = null)
        => Assert.IsTrue(rejections.Any(r => r.Code == code && (activityId is null || r.ActivityId == activityId)),
            $"expected {code} ({activityId}); got: {string.Join(", ", rejections.Select(r => $"{r.Code}({r.ActivityId})"))}");

    private static UserTask Task(string id, string? assignee = null, List<string>? groups = null, List<string>? users = null)
        => new(id, assignee, groups ?? [], users ?? [], null);

    // ── Validator ────────────────────────────────────────────────────────────

    [TestMethod]
    public void Validate_SameElementInTheTarget_NoRejection()
    {
        var run = Start(Linear(V1, Task("review")));
        Wait(run, "review");

        var rejections = Validate(run, Linear(V2, Task("review", assignee: "ann")));

        Assert.IsEmpty(rejections);
    }

    [TestMethod]
    public void Validate_FinishedInstance_NotRunning()
    {
        var run = Start(Linear(V1, Task("review")));
        run.State.Complete();

        AssertRejected(Validate(run, Linear(V2, Task("review"))), MigrationRejectionCodes.NotRunning);
    }

    [TestMethod]
    public void Validate_TargetIsTheCurrentVersion_SameVersion()
    {
        var run = Start(Linear(V1, Task("review")));

        AssertRejected(run.Execution.ValidateMigration(new MigrationPlan(V1, []), Linear(V1, Task("review"))),
            MigrationRejectionCodes.SameVersion);
    }

    [TestMethod]
    public void Validate_TargetOfAnotherProcess_OtherProcess()
    {
        var run = Start(Linear(V1, Task("review")));
        var other = Process("invoice:1:20261102T000000.0000000Z", [new StartEvent("start")], workflowId: "invoice");

        AssertRejected(Validate(run, other), MigrationRejectionCodes.OtherProcess);
    }

    [TestMethod]
    public void Validate_ActiveElementMissingInTheTarget_UnmappedActivity()
    {
        var run = Start(Linear(V1, Task("review")));
        Wait(run, "review");

        AssertRejected(Validate(run, Linear(V2, Task("approve"))), MigrationRejectionCodes.UnmappedActivity, "review");
    }

    [TestMethod]
    public void Validate_ExplicitMappingCoversARename()
    {
        var run = Start(Linear(V1, Task("review")));
        Wait(run, "review");

        var rejections = Validate(run, Linear(V2, Task("approve")), Plan(mappings: new ActivityMapping("review", "approve")));

        Assert.IsEmpty(rejections);
    }

    [TestMethod]
    public void Validate_MappingToAnUnknownElement_InvalidMapping()
    {
        var run = Start(Linear(V1, Task("review")));

        AssertRejected(Validate(run, Linear(V2, Task("review")), Plan(mappings: new ActivityMapping("review", "nope"))),
            MigrationRejectionCodes.InvalidMapping, "review");
    }

    [TestMethod]
    public void Validate_ElementChangesType_TypeChanged()
    {
        var run = Start(Linear(V1, Task("review")));
        Wait(run, "review");

        AssertRejected(Validate(run, Linear(V2, new TaskActivity("review"))), MigrationRejectionCodes.TypeChanged, "review");
    }

    [TestMethod]
    public void Validate_ElementMovesIntoASubProcess_ScopeChanged()
    {
        var run = Start(Linear(V1, Task("review")));
        Wait(run, "review");
        var sub = new SubProcess("sub") { Activities = [Task("review")] };

        AssertRejected(Validate(run, Linear(V2, sub)), MigrationRejectionCodes.ScopeChanged, "review");
    }

    [TestMethod]
    public void Validate_ParallelBecomesSequential_MultiInstanceChanged()
    {
        var run = Start(Linear(V1, new MultiInstanceActivity("review", Task("review"), IsSequential: false, LoopCardinality: 2)));
        var host = Wait(run, "review");
        Wait(run, "review", scopeId: host.ActivityInstanceId, multiInstanceIndex: 0);

        var target = Linear(V2, new MultiInstanceActivity("review", Task("review"), IsSequential: true, LoopCardinality: 2));
        AssertRejected(Validate(run, target), MigrationRejectionCodes.MultiInstanceChanged, "review");
    }

    [TestMethod]
    public void Validate_JoinWithAWaitingTokenGetsOtherIncomingFlows_JoinChanged()
    {
        Running Fixture(string pd, string secondBranch, out WorkflowDefinition definition)
        {
            var start = new StartEvent("start");
            var fork = new ParallelGateway("fork", IsFork: true);
            var a = new TaskActivity("a");
            var b = new TaskActivity(secondBranch);
            var join = new ParallelGateway("join", IsFork: false);
            var end = new EndEvent("end");
            definition = Process(pd, [start, fork, a, b, join, end],
            [
                new("f1", start, fork), new("f2", fork, a), new("f3", fork, b),
                new("f4", a, join), new("f5", b, join), new("f6", join, end),
            ]);
            return Start(definition);
        }
        var run = Fixture(V1, "b", out _);
        Wait(run, "join");
        Fixture(V2, "c", out var target);

        AssertRejected(Validate(run, target), MigrationRejectionCodes.JoinChanged, "join");
    }

    [TestMethod]
    public void Validate_ScriptRunning_InFlightWork()
    {
        var run = Start(Linear(V1, new ScriptTask("calc", "1")));
        Wait(run, "calc");

        AssertRejected(Validate(run, Linear(V2, new ScriptTask("calc", "2"))), MigrationRejectionCodes.InFlightWork, "calc");
    }

    [TestMethod]
    public void Validate_CompensationRunning_CompensationRunning()
    {
        var run = Start(Linear(V1, Task("review")));
        run.State.StartCompensationWalk(null, new CompensationWalkState(null, null, Guid.NewGuid()));

        AssertRejected(Validate(run, Linear(V2, Task("review"))), MigrationRejectionCodes.CompensationRunning);
    }

    [TestMethod]
    public void Validate_CallActivityCallsAnotherProcess_CallActivityChanged()
    {
        var run = Start(Linear(V1, new CallActivity("call", "billing", [], [])));
        Wait(run, "call");

        AssertRejected(Validate(run, Linear(V2, new CallActivity("call", "shipping", [], []))),
            MigrationRejectionCodes.CallActivityChanged, "call");
    }

    [TestMethod]
    public void Validate_CustomTaskOfAnotherType_ExternalTaskTypeChanged()
    {
        var run = Start(Linear(V1, new CustomTaskActivity("charge", "stripe")));
        Wait(run, "charge");

        AssertRejected(Validate(run, Linear(V2, new CustomTaskActivity("charge", "paypal"))),
            MigrationRejectionCodes.ExternalTaskTypeChanged, "charge");
    }

    [TestMethod]
    public void Validate_ProcessEventSubProcessStartChanges_EventSubProcessChanged()
    {
        EventSubProcess Esp(string duration) => new("esp")
        {
            Activities = [new TimerStartEvent("espStart", new TimerDefinition(TimerType.Duration, duration)), new EndEvent("espEnd")],
            IsInterrupting = false,
        };
        var run = Start(Linear(V1, Task("review"), Esp("PT1H")));

        AssertRejected(Validate(run, Linear(V2, Task("review"), Esp("PT2H"))), MigrationRejectionCodes.EventSubProcessChanged);
    }

    // ── Transition, Apply and replay ─────────────────────────────────────────

    [TestMethod]
    public void Migrate_RenamesRunningEntries_KeepsHistory_RecordsTheMigration()
    {
        // Arrange
        var run = Start(Linear(V1, Task("review")));
        var startId = run.State.Entries.First(e => e.IsCompleted).ActivityId;
        var waiting = Wait(run, "review");
        var target = Linear(V2, Task("approve"));

        // Act
        run.Execution.Migrate(Plan(mappings: new ActivityMapping("review", "approve")), target, "m1");

        // Assert
        Assert.AreEqual(V2, run.State.ProcessDefinitionId);
        Assert.AreEqual("approve", run.State.GetEntry(waiting.ActivityInstanceId).ActivityId);
        Assert.AreEqual(startId, run.State.Entries.First(e => e.IsCompleted).ActivityId, "finished entries keep their source ids");
        var migration = run.State.Migrations.Single();
        Assert.AreEqual("m1", migration.MigrationId);
        Assert.AreEqual(V1, migration.SourceProcessDefinitionId);
        Assert.AreEqual(new ActivityMapping("review", "approve"), migration.Mappings.Single());
        var migrated = run.Execution.GetUncommittedEvents().OfType<InstanceMigrated>().Single();
        Assert.AreEqual(new ActivityMapping("review", "approve"), migrated.Mappings.Single(), "only id-changing pairs are journaled");
    }

    [TestMethod]
    public void Migrate_Rejected_ThrowsAndEmitsNothing()
    {
        var run = Start(Linear(V1, Task("review")));
        Wait(run, "review");

        var ex = Assert.ThrowsExactly<InstanceMigrationRejectedException>(
            () => run.Execution.Migrate(Plan(), Linear(V2, Task("approve")), "m1"));

        Assert.AreEqual(MigrationRejectionCodes.UnmappedActivity, ex.Rejections.Single().Code);
        Assert.IsEmpty(run.Execution.GetUncommittedEvents());
        Assert.AreEqual(V1, run.State.ProcessDefinitionId);
    }

    [TestMethod]
    public void Replay_OfTheJournal_ReproducesTheMigratedState()
    {
        // Arrange: a whole run through the aggregate, so its events form a complete journal.
        var source = Linear(V1, Task("review"));
        var state = new WorkflowInstanceState();
        var execution = new WorkflowExecution(state, source);
        var journal = new List<WorkflowEvent>();
        execution.CreateAndStart(Guid.NewGuid());
        var start = state.Entries.Single();
        execution.MarkExecuting(start.ActivityInstanceId);
        execution.MarkCompleted(start.ActivityInstanceId, new ExpandoObject());
        execution.ResolveTransitions([new CompletedActivityTransitions(start.ActivityInstanceId, "start",
            [new ActivityTransition(source.GetActivity("review"))])]);
        var review = state.GetActiveActivities().Single();
        execution.MarkExecuting(review.ActivityInstanceId);
        execution.Migrate(Plan(mappings: new ActivityMapping("review", "approve")), Linear(V2, Task("approve")), "m1");
        journal.AddRange(execution.GetUncommittedEvents());

        // Act
        var replayed = new WorkflowInstanceState();
        var replay = new WorkflowExecution(replayed);
        foreach (var e in journal)
            replay.ReplayEvent(e);

        // Assert
        Assert.AreEqual(V2, replayed.ProcessDefinitionId);
        Assert.AreEqual("approve", replayed.GetActiveActivities().Single().ActivityId);
        Assert.AreEqual("start", replayed.Entries.First(e => e.IsCompleted).ActivityId);
        Assert.AreEqual("m1", replayed.Migrations.Single().MigrationId);
        Assert.AreEqual(state.Migrations.Single().MigratedAt, replayed.Migrations.Single().MigratedAt, "the time comes from the event");
    }

    // ── Rewiring waits: timers ───────────────────────────────────────────────

    private static TimerIntermediateCatchEvent Timer(string id, string duration) => new(id, new TimerDefinition(TimerType.Duration, duration));

    [TestMethod]
    public void Migrate_ChangedTimer_KeepsItsReminderByDefault()
    {
        var run = Start(Linear(V1, Timer("wait", "PT1H")));
        Wait(run, "wait");

        var effects = run.Execution.Migrate(Plan(), Linear(V2, Timer("wait", "PT5M")), "m1");

        Assert.IsEmpty(effects, "migration never moves a deadline silently");
    }

    [TestMethod]
    public void Migrate_ChangedTimer_UpdateEventTriggers_ReArmsFromTheTarget()
    {
        var run = Start(Linear(V1, Timer("wait", "PT1H")));
        var entry = Wait(run, "wait");

        var effects = run.Execution.Migrate(Plan(updateEventTriggers: true), Linear(V2, Timer("wait", "PT5M")), "m1");

        Assert.HasCount(2, effects);
        Assert.AreEqual(new UnregisterTimerEffect(run.State.Id, entry.ActivityInstanceId, "wait"), effects[0]);
        Assert.AreEqual(new RegisterTimerEffect(run.State.Id, entry.ActivityInstanceId, "wait", TimeSpan.FromMinutes(5)), effects[1]);
    }

    [TestMethod]
    public void Migrate_RenamedTimer_MovesTheReminderWithItsDeadline()
    {
        var run = Start(Linear(V1, Timer("wait", "PT1H")));
        var entry = Wait(run, "wait", since: DateTimeOffset.UtcNow.AddMinutes(-20));

        var effects = run.Execution.Migrate(Plan(mappings: new ActivityMapping("wait", "pause")),
            Linear(V2, Timer("pause", "PT1H")), "m1");

        Assert.AreEqual(new UnregisterTimerEffect(run.State.Id, entry.ActivityInstanceId, "wait"), effects[0]);
        var register = (RegisterTimerEffect)effects[1];
        Assert.AreEqual("pause", register.TimerActivityId);
        Assert.IsTrue(register.DueTime is { TotalMinutes: > 39 and <= 40 }, $"40 minutes are left, got {register.DueTime}");
    }

    [TestMethod]
    public void Migrate_BoundaryTimerRemovedAndAnotherAdded_UnregistersAndRegisters()
    {
        var host = Task("review");
        var run = Start(Linear(V1, host, new BoundaryTimerEvent("remind", "review", new TimerDefinition(TimerType.Duration, "PT1H"), false)));
        var entry = Wait(run, "review");
        var target = Linear(V2, Task("review"), new BoundaryTimerEvent("escalate", "review", new TimerDefinition(TimerType.Duration, "P1D")));

        var effects = run.Execution.Migrate(Plan(), target, "m1");

        CollectionAssert.AreEqual(new IInfrastructureEffect[]
        {
            new UnregisterTimerEffect(run.State.Id, entry.ActivityInstanceId, "remind"),
            new RegisterTimerEffect(run.State.Id, entry.ActivityInstanceId, "escalate", TimeSpan.FromDays(1)),
        }, effects.ToList());
    }

    [TestMethod]
    public void Migrate_NonInterruptingBoundaryThatAlreadyFired_IsNotReArmed()
    {
        var run = Start(Linear(V1, Task("review"), new BoundaryTimerEvent("remind", "review", new TimerDefinition(TimerType.Duration, "PT1H"), false)));
        Wait(run, "review", since: DateTimeOffset.UtcNow.AddHours(-2));
        var fired = Wait(run, "remind");
        run.State.CompleteEntry(fired.ActivityInstanceId);

        var effects = run.Execution.Migrate(Plan(mappings: new ActivityMapping("remind", "nudge")),
            Linear(V2, Task("review"), new BoundaryTimerEvent("nudge", "review", new TimerDefinition(TimerType.Duration, "PT1H"), false)), "m1");

        Assert.IsEmpty(effects);
    }

    // ── Rewiring waits: messages, signals, conditions ────────────────────────

    [TestMethod]
    public void Migrate_RenamedMessageCatch_ResubscribesUnderTheNewId()
    {
        var run = Start(Linear(V1, new MessageIntermediateCatchEvent("paid", "msg")));
        var entry = Wait(run, "paid");

        var effects = run.Execution.Migrate(Plan(mappings: new ActivityMapping("paid", "paymentReceived")),
            Linear(V2, new MessageIntermediateCatchEvent("paymentReceived", "msg")), "m1");

        CollectionAssert.AreEqual(new IInfrastructureEffect[]
        {
            new UnsubscribeMessageEffect("OrderPaid", "o-1"),
            new SubscribeMessageEffect("OrderPaid", "o-1", run.State.Id, "paymentReceived", entry.ActivityInstanceId),
        }, effects.ToList());
    }

    [TestMethod]
    public void Migrate_MessageOfTheCatchChanged_KeptUnlessUpdateEventTriggers()
    {
        var run = Start(Linear(V1, new MessageIntermediateCatchEvent("paid", "msg")));
        var entry = Wait(run, "paid");
        var target = Linear(V2, new MessageIntermediateCatchEvent("paid", "msg2"));

        Assert.IsEmpty(run.Execution.ValidateMigration(Plan(), target));
        var effects = run.Execution.Migrate(Plan(updateEventTriggers: true), target, "m1");

        CollectionAssert.AreEqual(new IInfrastructureEffect[]
        {
            new UnsubscribeMessageEffect("OrderPaid", "o-1"),
            new SubscribeMessageEffect("OrderShipped", "o-1", run.State.Id, "paid", entry.ActivityInstanceId),
        }, effects.ToList());
    }

    [TestMethod]
    public void Migrate_NewSignalBoundary_Subscribes()
    {
        var run = Start(Linear(V1, Task("review")));
        var entry = Wait(run, "review");

        var effects = run.Execution.Migrate(Plan(),
            Linear(V2, Task("review"), new SignalBoundaryEvent("abort", "review", "sig")), "m1");

        CollectionAssert.AreEqual(new IInfrastructureEffect[]
        {
            new SubscribeSignalEffect("Alarm", run.State.Id, "abort", entry.ActivityInstanceId),
        }, effects.ToList());
    }

    [TestMethod]
    public void Migrate_NewConditionalBoundary_RegistersAWatcher()
    {
        var run = Start(Linear(V1, Task("review")));
        var entry = Wait(run, "review");

        run.Execution.Migrate(Plan(), Linear(V2, Task("review"), new ConditionalBoundaryEvent("tooLate", "review", "late == true")), "m1");

        var watcher = run.State.ConditionalWatchers.Single();
        Assert.AreEqual(entry.ActivityInstanceId, watcher.ActivityInstanceId);
        Assert.AreEqual("tooLate", watcher.ActivityId);
        Assert.AreEqual("late == true", watcher.ConditionExpression);
    }

    // ── User tasks ───────────────────────────────────────────────────────────

    private static ActivityInstanceEntry WaitingUserTask(Running run, string id, UserTask definition)
    {
        var entry = Wait(run, id);
        run.Execution.ProcessCommands([new RegisterUserTaskCommand(definition.Assignee, definition.CandidateGroups,
            definition.CandidateUsers, definition.ExpectedOutputVariables)], entry.ActivityInstanceId);
        run.Execution.ClearUncommittedEvents();
        return entry;
    }

    [TestMethod]
    public void Migrate_UserTaskCandidatesChange_RedefinesIt_KeepsAQualifiedClaim()
    {
        var source = Task("review", users: ["ann", "bob"]);
        var run = Start(Linear(V1, source));
        var entry = WaitingUserTask(run, "review", source);
        run.Execution.ClaimUserTask(entry.ActivityInstanceId, "ann", []);

        var effects = run.Execution.Migrate(Plan(), Linear(V2, Task("review", users: ["ann", "carl"])), "m1");

        var task = run.State.UserTasks[entry.ActivityInstanceId];
        CollectionAssert.AreEqual(new[] { "ann", "carl" }, task.CandidateUsers.ToList());
        Assert.AreEqual("ann", task.ClaimedBy);
        var redefine = effects.OfType<RedefineUserTaskEffect>().Single();
        Assert.AreEqual("review", redefine.ActivityId);
        Assert.IsFalse(effects.OfType<UpdateUserTaskClaimEffect>().Any());
    }

    [TestMethod]
    public void Migrate_ClaimantNoLongerQualifies_UnclaimsTheTask()
    {
        var source = Task("review", users: ["ann", "bob"]);
        var run = Start(Linear(V1, source));
        var entry = WaitingUserTask(run, "review", source);
        run.Execution.ClaimUserTask(entry.ActivityInstanceId, "bob", []);

        var effects = run.Execution.Migrate(Plan(), Linear(V2, Task("review", users: ["ann"])), "m1");

        Assert.AreEqual(UserTaskLifecycleState.Created, run.State.UserTasks[entry.ActivityInstanceId].TaskState);
        Assert.AreEqual(new UpdateUserTaskClaimEffect(entry.ActivityInstanceId, null, UserTaskLifecycleState.Created),
            effects.OfType<UpdateUserTaskClaimEffect>().Single());
    }

    [TestMethod]
    public void Migrate_RenamedUserTask_SameAssignment_OnlyMovesTheReadModelRow()
    {
        var source = Task("review", assignee: "ann");
        var run = Start(Linear(V1, source));
        var entry = WaitingUserTask(run, "review", source);

        var effects = run.Execution.Migrate(Plan(mappings: new ActivityMapping("review", "approve")),
            Linear(V2, Task("approve", assignee: "ann")), "m1");

        Assert.AreEqual("approve", effects.OfType<RedefineUserTaskEffect>().Single().ActivityId);
        Assert.IsFalse(run.Execution.GetUncommittedEvents().OfType<UserTaskRedefined>().Any());
    }

    // ── Callbacks after a migration ──────────────────────────────────────────

    [TestMethod]
    public void TimerFiredUnderTheOldId_AfterARename_CompletesTheRenamedElement()
    {
        // Arrange
        var run = Start(Linear(V1, Timer("wait", "PT1H")));
        var entry = Wait(run, "wait");
        var target = Linear(V2, Timer("pause", "PT1H"));
        run.Execution.Migrate(Plan(mappings: new ActivityMapping("wait", "pause")), target, "m1");
        var migrated = new WorkflowExecution(run.State, target);

        // Act: a reminder armed before the migration races it.
        Assert.IsFalse(migrated.IsTimerFireStale("wait", entry.ActivityInstanceId));
        migrated.HandleTimerFired("wait", entry.ActivityInstanceId);

        // Assert
        Assert.IsTrue(run.State.GetEntry(entry.ActivityInstanceId).IsCompleted);
    }

    [TestMethod]
    public void TimerFiredForARemovedBoundary_AfterMigration_IsIgnored()
    {
        // Arrange
        var run = Start(Linear(V1, Task("review"), new BoundaryTimerEvent("remind", "review", new TimerDefinition(TimerType.Duration, "PT1H"))));
        var entry = Wait(run, "review");
        var target = Linear(V2, Task("review"));
        run.Execution.Migrate(Plan(), target, "m1");
        var migrated = new WorkflowExecution(run.State, target);

        // Act
        var stale = migrated.IsTimerFireStale("remind", entry.ActivityInstanceId);
        var effects = migrated.HandleTimerFired("remind", entry.ActivityInstanceId);

        // Assert
        Assert.IsTrue(stale);
        Assert.IsEmpty(effects);
        Assert.IsFalse(run.State.GetEntry(entry.ActivityInstanceId).IsCompleted);
    }

    [TestMethod]
    public void ChildCompletedUnderTheOldCallActivityId_CompletesTheRenamedElement()
    {
        var run = Start(Linear(V1, new CallActivity("call", "billing", [], [])));
        var entry = Wait(run, "call");
        var target = Linear(V2, new CallActivity("bill", "billing", [], []));
        run.Execution.Migrate(Plan(mappings: new ActivityMapping("call", "bill")), target, "m1");
        var migrated = new WorkflowExecution(run.State, target);

        migrated.OnChildWorkflowCompleted("call", new ExpandoObject());

        Assert.IsTrue(run.State.GetEntry(entry.ActivityInstanceId).IsCompleted);
    }

    [TestMethod]
    public void ResolveMigratedActivityId_FollowsEveryMigration()
    {
        var state = new WorkflowInstanceState();
        state.ApplyMigration("m1", V1, V2, [new ActivityMapping("a", "b")], DateTimeOffset.UtcNow);
        state.ApplyMigration("m2", V2, "order:3:20261103T000000.0000000Z", [new ActivityMapping("b", "c")], DateTimeOffset.UtcNow);

        Assert.AreEqual("c", state.ResolveMigratedActivityId("a"));
        Assert.AreEqual("c", state.ResolveMigratedActivityId("b"));
        Assert.AreEqual("x", state.ResolveMigratedActivityId("x"));
    }
}
