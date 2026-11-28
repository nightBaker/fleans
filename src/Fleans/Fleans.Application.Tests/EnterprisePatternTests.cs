using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Patterns enterprise processes lean on that no other test pins: a saga rolled back from an error
/// event sub-process, a rollback that itself fails, repeating reminders, an at-least-once timer
/// tick, and a non-interrupting boundary that receives more than one message. A test marked
/// <c>Ignore("Blocked by #NNN")</c> fails on the engine as it is, until that issue is fixed.
/// </summary>
[TestClass]
public class EnterprisePatternTests : WorkflowTestBase
{
    private static ScriptTask AutoTask(string id) => new(id, "PASS", "csharp");

    // ---------- Saga: compensation triggered from an error event sub-process ----------

    /// <summary>
    /// start → book (compensable) → charge (fails) → end, with an interrupting error event
    /// sub-process whose handler is <paramref name="compensationTrigger"/> (+ an end event after a throw).
    /// </summary>
    private static WorkflowDefinition SagaWorkflow(Activity compensationTrigger, bool rollbackFails = false)
    {
        var start = new StartEvent("start");
        var book = AutoTask("book");
        var cancelBooking = new ScriptTask("cancelBooking", rollbackFails ? "FAIL" : "PASS", "csharp");
        var charge = new ScriptTask("charge", "FAIL", "csharp");
        var end = new EndEvent("end");

        var errStart = new ErrorStartEvent("errStart", null);
        var espActivities = new List<Activity> { errStart, compensationTrigger };
        var espFlows = new List<SequenceFlow> { new("esp_f1", errStart, compensationTrigger) };
        if (compensationTrigger is not CompensationEndEvent)
        {
            var errEnd = new EndEvent("errEnd");
            espActivities.Add(errEnd);
            espFlows.Add(new SequenceFlow("esp_f2", compensationTrigger, errEnd));
        }

        return new WorkflowDefinition
        {
            WorkflowId = "saga-from-error-esp",
            Activities =
            [
                start, book, charge, end,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"), cancelBooking,
                new EventSubProcess("rollback")
                {
                    Activities = espActivities,
                    SequenceFlows = espFlows,
                    IsInterrupting = true
                }
            ],
            SequenceFlows =
            [
                new("f1", start, book),
                new("f2", book, charge),
                new("f3", charge, end)
            ]
        };
    }

    [TestMethod]
    public async Task CompensationThrow_InsideAnErrorEventSubProcess_CompensatesTheEnclosingScope()
    {
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(SagaWorkflow(new CompensationIntermediateThrowEvent("undo", null)));

        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "undo"), "the throw ran");
        Assert.IsTrue(
            snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelBooking" && a.ErrorState is null),
            "the booking completed before the failure, so the rollback must run its compensation handler");
    }

    [TestMethod]
    public async Task CompensationEnd_InsideAnErrorEventSubProcess_CompensatesTheEnclosingScope()
    {
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(SagaWorkflow(new CompensationEndEvent("undo")));

        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(
            snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelBooking" && a.ErrorState is null),
            "the booking completed before the failure, so the rollback must run its compensation handler");
    }

    [TestMethod]
    public async Task CompensationEnd_InsideASubProcess_CompensatesAndClosesTheSubProcess()
    {
        // start → order( subStart → book → undo (compensation end) ) → after → end
        var start = new StartEvent("start");
        var subStart = new StartEvent("subStart");
        var book = AutoTask("book");
        var undo = new CompensationEndEvent("undo");
        var order = new SubProcess("order")
        {
            Activities =
            [
                subStart, book, undo,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"), AutoTask("cancelBooking")
            ],
            SequenceFlows = [new("s1", subStart, book), new("s2", book, undo)]
        };
        var after = AutoTask("after");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "compensation-end-in-subprocess",
            Activities = [start, order, after, end],
            SequenceFlows = [new("f1", start, order), new("f2", order, after), new("f3", after, end)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelBooking" && a.ErrorState is null),
            "the compensation end event runs the handler");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "after"),
            "the sub-process closes after the walk and the flow goes on");
    }

    [TestMethod]
    public async Task CompensationThrow_InsideAnErrorEventSubProcessOfASubProcess_CompensatesOnlyThatSubProcess()
    {
        // start → outerBook (compensable) → order( subStart → book (compensable) → charge (fails) ) → after → end
        // The sub-process's error event sub-process throws compensation: only "book" is its to undo.
        var start = new StartEvent("start");
        var outerBook = AutoTask("outerBook");
        var subStart = new StartEvent("subStart");
        var book = AutoTask("book");
        var charge = new ScriptTask("charge", "FAIL", "csharp");
        var subEnd = new EndEvent("subEnd");
        var errStart = new ErrorStartEvent("errStart", null);
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var errEnd = new EndEvent("errEnd");
        var order = new SubProcess("order")
        {
            Activities =
            [
                subStart, book, charge, subEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"), AutoTask("cancelBooking"),
                new EventSubProcess("rollback")
                {
                    Activities = [errStart, undo, errEnd],
                    SequenceFlows = [new("e1", errStart, undo), new("e2", undo, errEnd)],
                    IsInterrupting = true
                }
            ],
            SequenceFlows = [new("s1", subStart, book), new("s2", book, charge), new("s3", charge, subEnd)]
        };
        var after = AutoTask("after");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "saga-in-subprocess",
            Activities =
            [
                start, outerBook, order, after, end,
                new CompensationBoundaryEvent("cbOuter", "outerBook", "cancelOuter"), AutoTask("cancelOuter")
            ],
            SequenceFlows =
            [
                new("f1", start, outerBook), new("f2", outerBook, order),
                new("f3", order, after), new("f4", after, end)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelBooking" && a.ErrorState is null),
            "the sub-process's own completed step is compensated");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelOuter"),
            "a step of the process around the sub-process is not this walk's to undo");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "after"),
            "the handled failure lets the flow go on after the sub-process");
    }

    [TestMethod]
    [Ignore("Blocked by #1141")]
    public async Task TwoCompensationThrowers_InOneScope_CompensateOnce_AndBothComplete()
    {
        var start = new StartEvent("start");
        var book = AutoTask("book");
        var fork = new ParallelGateway("fork", IsFork: true);
        var undo1 = new CompensationIntermediateThrowEvent("undo1", null);
        var undo2 = new CompensationIntermediateThrowEvent("undo2", null);
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "two-compensation-throwers",
            Activities =
            [
                start, book, fork, undo1, undo2, end1, end2,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"), AutoTask("cancelBooking")
            ],
            SequenceFlows =
            [
                new("f1", start, book), new("f2", book, fork),
                new("f3", fork, undo1), new("f4", fork, undo2),
                new("f5", undo1, end1), new("f6", undo2, end2)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "cancelBooking"),
            "the booking is cancelled once");
    }

    // ---------- A compensation handler that fails ----------

    [TestMethod]
    public async Task FailedCompensationHandler_FailsTheInstance()
    {
        var start = new StartEvent("start");
        var book = AutoTask("book");
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback",
            Activities =
            [
                start, book, undo, end,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("f1", start, book), new("f2", book, undo), new("f3", undo, end)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed,
            "a rollback that failed left the booking in place: the instance must end as failed, not as completed");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_DoesNotEndTheInstance_WhileAnotherBranchIsActive()
    {
        var start = new StartEvent("start");
        var book = AutoTask("book");
        var fork = new ParallelGateway("fork", IsFork: true);
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var undoEnd = new EndEvent("undoEnd");
        var review = new TaskActivity("review");
        var reviewEnd = new EndEvent("reviewEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-parallel",
            Activities =
            [
                start, book, fork, undo, undoEnd, review, reviewEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows =
            [
                new("f1", start, book), new("f2", book, fork),
                new("f3", fork, undo), new("f4", undo, undoEnd),
                new("f5", fork, review), new("f6", review, reviewEnd)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(),
            s => s.CompletedActivities.Any(a => a.ActivityId == "cancelBooking"));

        Assert.IsFalse(snapshot.IsCompleted && snapshot.ActiveActivities.Count > 0,
            "an instance must not be terminal while 'review' is still active");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_OfACompensationEndEvent_FailsTheInstance()
    {
        var start = new StartEvent("start");
        var book = AutoTask("book");
        var undo = new CompensationEndEvent("undo", null);
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-at-end",
            Activities =
            [
                start, book, undo,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("f1", start, book), new("f2", book, undo)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed, "an end event whose compensation failed does not complete the instance");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_InAChildProcess_FailsTheCallActivityOfItsParent()
    {
        var childStart = new StartEvent("start");
        var book = AutoTask("book");
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var childEnd = new EndEvent("end");
        var child = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-child",
            Activities =
            [
                childStart, book, undo, childEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("f1", childStart, book), new("f2", book, undo), new("f3", undo, childEnd)]
        };
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("failed-rollback-child")
            .DeployVersion(child, "<xml/>");

        var start = new StartEvent("start");
        var call = new CallActivity("call", "failed-rollback-child", [], []);
        var end = new EndEvent("end");
        var rollbackFailed = new BoundaryErrorEvent("rollbackFailed", "call", null);
        var escalate = AutoTask("escalate");
        var escalated = new EndEvent("escalated");
        var parent = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-parent",
            Activities = [start, call, end, rollbackFailed, escalate, escalated],
            SequenceFlows =
            [
                new("p1", start, call), new("p2", call, end),
                new("p3", rollbackFailed, escalate), new("p4", escalate, escalated)
            ]
        };
        var parentDefinition = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("failed-rollback-parent");
        await parentDefinition.DeployVersion(parent, "<xml/>");

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(await parentDefinition.GetLatestDefinition());
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "escalate"),
            "the parent is told once that the child failed, and its error boundary catches it");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "end"),
            "the parent does not continue as if the child had completed");
    }

    [TestMethod]
    public async Task FailedActivity_InsideASubProcess_WithoutAnErrorBoundary_FailsTheInstance()
    {
        var subStart = new StartEvent("subStart");
        var work = new ScriptTask("work", "FAIL", "csharp");
        var subEnd = new EndEvent("subEnd");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, work, subEnd],
            SequenceFlows = [new("s1", subStart, work), new("s2", work, subEnd)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "unhandled-failure-in-sub-process",
            Activities = [start, sub, end],
            SequenceFlows = [new("f1", start, sub), new("f2", sub, end)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed, "nothing catches the failure, so the instance must end as failed");
    }

    private static (WorkflowDefinition Workflow, SubProcess Sub) AroundSubProcess(
        string workflowId, List<Activity> subActivities, List<SequenceFlow> subFlows)
    {
        var sub = new SubProcess("sub") { Activities = subActivities, SequenceFlows = subFlows };
        var start = new StartEvent("start");
        var after = AutoTask("after");
        var end = new EndEvent("end");
        return (new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, sub, after, end],
            SequenceFlows = [new("f1", start, sub), new("f2", sub, after), new("f3", after, end)]
        }, sub);
    }

    [TestMethod]
    public async Task FailedActivity_CaughtByABoundaryInsideASubProcess_LetsTheSubProcessComplete()
    {
        var subStart = new StartEvent("subStart");
        var work = new ScriptTask("work", "FAIL", "csharp");
        var subEnd = new EndEvent("subEnd");
        var onError = new BoundaryErrorEvent("onError", "work", null);
        var fallback = AutoTask("fallback");
        var fallbackEnd = new EndEvent("fallbackEnd");
        var (workflow, _) = AroundSubProcess("caught-failure-in-sub-process",
            [subStart, work, subEnd, onError, fallback, fallbackEnd],
            [new("s1", subStart, work), new("s2", work, subEnd), new("s3", onError, fallback), new("s4", fallback, fallbackEnd)]);

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsFalse(snapshot.IsFailed, "the failure was caught inside the sub-process");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "after"),
            "the sub-process completes through the fallback path and the process goes on");
        Assert.IsNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "sub").ErrorState,
            "the sub-process itself did not fail");
    }

    [TestMethod]
    public async Task FailedActivity_OnOneBranchOfASubProcess_LetsItCompleteThroughTheOtherBranch()
    {
        var subStart = new StartEvent("subStart");
        var fork = new ParallelGateway("fork", IsFork: true);
        var work = new ScriptTask("work", "FAIL", "csharp");
        var workEnd = new EndEvent("workEnd");
        var other = AutoTask("other");
        var otherEnd = new EndEvent("otherEnd");
        var (workflow, _) = AroundSubProcess("failed-branch-in-sub-process",
            [subStart, fork, work, workEnd, other, otherEnd],
            [
                new("s1", subStart, fork), new("s2", fork, work), new("s3", work, workEnd),
                new("s4", fork, other), new("s5", other, otherEnd)
            ]);

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsFalse(snapshot.IsFailed,
            "a branch that reached its end completes the sub-process, as it completes a process at the root");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "after"));
    }

    [TestMethod]
    public async Task FailedActivity_InsideNestedSubProcesses_FailsEachOfThem_AndTheInstance()
    {
        var innerStart = new StartEvent("innerStart");
        var work = new ScriptTask("work", "FAIL", "csharp");
        var innerEnd = new EndEvent("innerEnd");
        var inner = new SubProcess("inner")
        {
            Activities = [innerStart, work, innerEnd],
            SequenceFlows = [new("i1", innerStart, work), new("i2", work, innerEnd)]
        };
        var subStart = new StartEvent("subStart");
        var subEnd = new EndEvent("subEnd");
        var (workflow, _) = AroundSubProcess("unhandled-failure-in-nested-sub-process",
            [subStart, inner, subEnd],
            [new("s1", subStart, inner), new("s2", inner, subEnd)]);

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed);
        Assert.IsEmpty(snapshot.ActiveActivities);
        foreach (var id in new[] { "work", "inner", "sub" })
            Assert.IsNotNull(snapshot.CompletedActivities.Single(a => a.ActivityId == id).ErrorState,
                $"'{id}' carries the failure");
    }

    [TestMethod]
    public async Task FailedActivity_InsideASubProcess_IsStillCaughtByItsErrorBoundary()
    {
        var subStart = new StartEvent("subStart");
        var work = new ScriptTask("work", "FAIL", "csharp");
        var subEnd = new EndEvent("subEnd");
        var (workflow, _) = AroundSubProcess("boundary-on-sub-process",
            [subStart, work, subEnd], [new("s1", subStart, work), new("s2", work, subEnd)]);
        var onError = new BoundaryErrorEvent("onError", "sub", null);
        var fallback = AutoTask("fallback");
        var fallbackEnd = new EndEvent("fallbackEnd");
        workflow.Activities.AddRange([onError, fallback, fallbackEnd]);
        workflow.SequenceFlows.AddRange([new("f4", onError, fallback), new("f5", fallback, fallbackEnd)]);

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsFalse(snapshot.IsFailed);
        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "fallback"),
            "the boundary fires once");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "after"));
    }

    [TestMethod]
    public async Task FailedActivity_InsideACompensationHandlerSubProcess_FailsTheInstance()
    {
        var handlerStart = new StartEvent("handlerStart");
        var refund = new ScriptTask("refund", "FAIL", "csharp");
        var handlerEnd = new EndEvent("handlerEnd");
        var cancelBooking = new SubProcess("cancelBooking")
        {
            Activities = [handlerStart, refund, handlerEnd],
            SequenceFlows = [new("h1", handlerStart, refund), new("h2", refund, handlerEnd)]
        };
        var start = new StartEvent("start");
        var book = AutoTask("book");
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-sub-process",
            Activities =
            [
                start, book, undo, end,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"), cancelBooking
            ],
            SequenceFlows = [new("f1", start, book), new("f2", book, undo), new("f3", undo, end)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed, "the handler sub-process failed, so the rollback and the instance fail");
        Assert.IsNotNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "undo").ErrorState);
    }

    [TestMethod]
    public async Task FailedCompensationHandler_InsideASubProcess_WithoutAnErrorBoundary_FailsTheInstance()
    {
        var subStart = new StartEvent("subStart");
        var book = AutoTask("book");
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var subEnd = new EndEvent("subEnd");
        var order = new SubProcess("order")
        {
            Activities =
            [
                subStart, book, undo, subEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("s1", subStart, book), new("s2", book, undo), new("s3", undo, subEnd)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-in-sub-process-uncaught",
            Activities = [start, order, end],
            SequenceFlows = [new("f1", start, order), new("f2", order, end)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed, "nothing catches the failed rollback, so the instance must end as failed");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_OfASagaRollback_FailsTheThrow_AndIsNotCaughtByItsOwnHandler()
    {
        var workflow = SagaWorkflow(new CompensationIntermediateThrowEvent("undo", null), rollbackFails: true);

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.AreEqual(1, snapshot.CompletedActivities.Count(a => a.ActivityId == "cancelBooking"),
            "the error handler that asked for the rollback does not catch its failure and start again");
        Assert.IsNotNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "undo").ErrorState,
            "the compensation throw failed with its handler");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "errEnd"),
            "the handler does not continue past the failed compensation");
    }

    [TestMethod]
    [Ignore("Blocked by #1142")]
    public async Task FailedActivity_InsideAnErrorEventSubProcess_FailsTheInstance()
    {
        var workflow = SagaWorkflow(new ScriptTask("notify", "FAIL", "csharp"));

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed,
            "the error handler itself failed and nothing caught it: the instance must end as failed");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_InsideASubProcess_IsCaughtByItsErrorBoundary()
    {
        var subStart = new StartEvent("subStart");
        var book = AutoTask("book");
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var subEnd = new EndEvent("subEnd");
        var order = new SubProcess("order")
        {
            Activities =
            [
                subStart, book, undo, subEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("s1", subStart, book), new("s2", book, undo), new("s3", undo, subEnd)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var rollbackFailed = new BoundaryErrorEvent("rollbackFailed", "order", null);
        var escalate = AutoTask("escalate");
        var escalated = new EndEvent("escalated");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-caught",
            Activities = [start, order, end, rollbackFailed, escalate, escalated],
            SequenceFlows =
            [
                new("f1", start, order), new("f2", order, end),
                new("f3", rollbackFailed, escalate), new("f4", escalate, escalated)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsFalse(snapshot.IsFailed, "the error boundary caught the failed rollback");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "escalate"),
            "the boundary path runs");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "subEnd"),
            "the sub-process does not continue past the failed compensation");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_OfACancelledTransaction_TakesTheErrorBoundary_NotTheCancelBoundary()
    {
        var txStart = new StartEvent("txStart");
        var book = AutoTask("book");
        var cancelEnd = new CancelEndEvent("cancelEnd");
        var tx = new Transaction("tx")
        {
            Activities =
            [
                txStart, book, cancelEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("t1", txStart, book), new("t2", book, cancelEnd)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var onCancel = new CancelBoundaryEvent("onCancel", "tx");
        var cancelled = AutoTask("cancelled");
        var cancelledEnd = new EndEvent("cancelledEnd");
        var onError = new BoundaryErrorEvent("onError", "tx", null);
        var escalate = AutoTask("escalate");
        var escalated = new EndEvent("escalated");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-in-transaction",
            Activities = [start, tx, end, onCancel, cancelled, cancelledEnd, onError, escalate, escalated],
            SequenceFlows =
            [
                new("f1", start, tx), new("f2", tx, end),
                new("f3", onCancel, cancelled), new("f4", cancelled, cancelledEnd),
                new("f5", onError, escalate), new("f6", escalate, escalated)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "escalate"),
            "a cancellation that could not be rolled back leaves through the error boundary");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelled"),
            "the transaction was not cleanly cancelled");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_OfAThrowInsideATransaction_TakesTheErrorBoundary()
    {
        var txStart = new StartEvent("txStart");
        var book = AutoTask("book");
        var undo = new CompensationIntermediateThrowEvent("undo", null);
        var txEnd = new EndEvent("txEnd");
        var tx = new Transaction("tx")
        {
            Activities =
            [
                txStart, book, undo, txEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("t1", txStart, book), new("t2", book, undo), new("t3", undo, txEnd)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var onError = new BoundaryErrorEvent("onError", "tx", null);
        var escalate = AutoTask("escalate");
        var escalated = new EndEvent("escalated");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-thrown-in-transaction",
            Activities = [start, tx, end, onError, escalate, escalated],
            SequenceFlows =
            [
                new("f1", start, tx), new("f2", tx, end),
                new("f3", onError, escalate), new("f4", escalate, escalated)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsFalse(snapshot.IsFailed, "the error boundary of the transaction caught the failed rollback");
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "escalate"),
            "the boundary path runs");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "txEnd"),
            "the transaction does not continue past the failed compensation");
    }

    [TestMethod]
    public async Task FailedCompensationHandler_OfACancelledTransaction_WithoutAnErrorBoundary_FailsTheInstance()
    {
        var txStart = new StartEvent("txStart");
        var book = AutoTask("book");
        var cancelEnd = new CancelEndEvent("cancelEnd");
        var tx = new Transaction("tx")
        {
            Activities =
            [
                txStart, book, cancelEnd,
                new CompensationBoundaryEvent("cbBook", "book", "cancelBooking"),
                new ScriptTask("cancelBooking", "FAIL", "csharp")
            ],
            SequenceFlows = [new("t1", txStart, book), new("t2", book, cancelEnd)]
        };
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var onCancel = new CancelBoundaryEvent("onCancel", "tx");
        var cancelled = AutoTask("cancelled");
        var cancelledEnd = new EndEvent("cancelledEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "failed-rollback-in-transaction-uncaught",
            Activities = [start, tx, end, onCancel, cancelled, cancelledEnd],
            SequenceFlows =
            [
                new("f1", start, tx), new("f2", tx, end),
                new("f3", onCancel, cancelled), new("f4", cancelled, cancelledEnd)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var snapshot = await WaitForCondition(grain.GetPrimaryKey(), s => s.IsCompleted);

        Assert.IsTrue(snapshot.IsFailed, "nothing catches the failed rollback, so the instance fails");
        Assert.IsFalse(snapshot.CompletedActivities.Any(a => a.ActivityId == "cancelled"),
            "the transaction was not cleanly cancelled");
    }

    // ---------- Repeating timer on a non-interrupting event sub-process ----------

    [TestMethod]
    [Ignore("Blocked by #1136")]
    public async Task NonInterruptingTimerEventSubProcess_WithABoundedCycle_StopsAfterItsRepetitions()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var remindStart = new TimerStartEvent("remindStart", new TimerDefinition(TimerType.Cycle, "R2/PT1H"));
        var remind = AutoTask("remind");
        var remindEnd = new EndEvent("remindEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "bounded-reminder-esp",
            Activities =
            [
                start, task, end,
                new EventSubProcess("reminder")
                {
                    Activities = [remindStart, remind, remindEnd],
                    SequenceFlows = [new("r1", remindStart, remind), new("r2", remind, remindEnd)],
                    IsInterrupting = false
                }
            ],
            SequenceFlows = [new("f1", start, task), new("f2", task, end)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var instanceId = grain.GetPrimaryKey();

        // HandleTimerFired returns the due time of the next repetition, or null when there is none.
        var afterFirst = await grain.HandleTimerFired("remindStart", instanceId);
        var afterSecond = await grain.HandleTimerFired("remindStart", instanceId);

        Assert.IsNotNull(afterFirst, "R2: one repetition is left after the first fire");
        Assert.IsNull(afterSecond, "R2: the second fire is the last one, nothing may be re-armed");
    }

    // ---------- At-least-once timer tick ----------

    [TestMethod]
    [Ignore("Blocked by #1137")]
    public async Task RedeliveredTimerTick_FiresANonInterruptingBoundaryTimerOnce()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var escalate = new TaskActivity("escalate");
        var escalateEnd = new EndEvent("escalateEnd");
        var sla = new BoundaryTimerEvent("sla", "task", new TimerDefinition(TimerType.Duration, "PT1H"), IsInterrupting: false);
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "redelivered-tick",
            Activities =
            [
                start, task, end, escalate, escalateEnd, sla
            ],
            SequenceFlows =
            [
                new("f1", start, task), new("f2", task, end),
                new("f3", sla, escalate),
                new("f4", escalate, escalateEnd)
            ]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var instanceId = grain.GetPrimaryKey();
        var host = (await QueryService.GetStateSnapshot(instanceId))!.ActiveActivities.Single(a => a.ActivityId == "task");

        // The callback grain delivers the tick again when the first call's reply is lost or its
        // reminder could not be removed; a one-shot (duration) timer is still due once.
        await grain.HandleTimerFired("sla", host.ActivityInstanceId);
        await grain.HandleTimerFired("sla", host.ActivityInstanceId);

        var snapshot = await QueryService.GetStateSnapshot(instanceId);
        Assert.AreEqual(1, snapshot!.ActiveActivities.Count(a => a.ActivityId == "escalate"),
            "one timer, one escalation");
    }

    // ---------- Non-interrupting boundary that fires more than once ----------

    [TestMethod]
    [Ignore("Blocked by #1138")]
    public async Task NonInterruptingMessageBoundary_ReceivesEveryMessage_WhileItsHostIsActive()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var boundary = new MessageBoundaryEvent("statusUpdate", "task", "msg", IsInterrupting: false);
        var record = new TaskActivity("record");
        var recordEnd = new EndEvent("recordEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "repeated-message-boundary",
            Activities = [start, task, end, boundary, record, recordEnd],
            SequenceFlows =
            [
                new("f1", start, task), new("f2", task, end),
                new("f3", boundary, record), new("f4", record, recordEnd)
            ],
            Messages = [new MessageDefinition("msg", "orderStatus", "orderId")]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        dynamic variables = new ExpandoObject();
        variables.orderId = "order-42";
        await grain.StartWorkflow(workflow, variables: (ExpandoObject)variables);
        var correlation = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(
            MessageCorrelationKey.Build("orderStatus", "order-42"));

        var first = await correlation.DeliverMessage(new ExpandoObject());
        var second = await correlation.DeliverMessage(new ExpandoObject());

        Assert.IsTrue(first, "the first status update is delivered");
        Assert.IsTrue(second, "the host is still active, so the second status update is delivered too");
        var snapshot = await QueryService.GetStateSnapshot(grain.GetPrimaryKey());
        Assert.AreEqual(2, snapshot!.ActiveActivities.Count(a => a.ActivityId == "record"),
            "each status update starts the boundary path");
    }

    [TestMethod]
    [Ignore("Blocked by #1138")]
    public async Task NonInterruptingSignalBoundary_ReceivesEveryBroadcast_WhileItsHostIsActive()
    {
        var start = new StartEvent("start");
        var task = new TaskActivity("task");
        var end = new EndEvent("end");
        var boundary = new SignalBoundaryEvent("priceChanged", "task", "sig", IsInterrupting: false);
        var record = new TaskActivity("record");
        var recordEnd = new EndEvent("recordEnd");
        var signalName = $"price-{Guid.NewGuid():N}";
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "repeated-signal-boundary",
            Activities = [start, task, end, boundary, record, recordEnd],
            SequenceFlows =
            [
                new("f1", start, task), new("f2", task, end),
                new("f3", boundary, record), new("f4", record, recordEnd)
            ],
            Signals = [new SignalDefinition("sig", signalName)]
        };

        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await grain.StartWorkflow(workflow);
        var instanceId = grain.GetPrimaryKey();

        Assert.AreEqual(1, await BroadcastSignalWhenSubscribed(signalName), "first broadcast");
        await WaitForCondition(instanceId, s => s.ActiveActivities.Any(a => a.ActivityId == "record"));
        // Gives a re-subscription, if the engine makes one, the same time the first subscription had.
        var second = await BroadcastSignalWhenSubscribed(signalName, timeoutMs: 5000);

        Assert.AreEqual(1, second, "the host is still active, so the second broadcast reaches it too");
        var snapshot = await WaitForCondition(instanceId,
            s => s.ActiveActivities.Count(a => a.ActivityId == "record") == 2, timeoutMs: 5000);
        Assert.AreEqual(2, snapshot.ActiveActivities.Count(a => a.ActivityId == "record"),
            "each broadcast starts the boundary path");
    }
}
