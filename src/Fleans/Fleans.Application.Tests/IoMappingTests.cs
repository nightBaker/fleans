using System.Dynamic;
using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Application.QueryModels;
using Fleans.Application.Scripts;
using Fleans.Application.Tests.CustomTasks.TestStubs;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Infrastructure.Conditions;
using Fleans.Infrastructure.Scripts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

/// <summary>
/// <c>fleans:ioMapping</c> on every element category (#948): inputs open an activity-local scope
/// the activity sees and the process does not; outputs, when defined, are the only variables that
/// reach the enclosing scope; an expression that does not evaluate fails the activity. Scripts run
/// on the real C# executor here, so they read the local scope as <c>_context</c>.
/// </summary>
[TestClass]
public class IoMappingTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<CSharpSiloConfigurator>();

    // --- Script task ---

    [TestMethod]
    public async Task ScriptTask_Inputs_AreVisibleToTheTask_ButNotToTheProcess()
    {
        var task = new ScriptTask("script1", "_context.seen = _context.scaled + 1")
        {
            InputMappings = [new InputMapping("=count * 10", "scaled")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-script-inputs", task), Vars(("count", 2L)));

        Assert.IsFalse(snapshot.IsFailed);
        var root = RootScope(snapshot);
        Assert.AreEqual(21L, Convert.ToInt64(await instance.GetVariable(root, "seen")));
        Assert.IsNull(await instance.GetVariable(root, "scaled"), "an input stays local to the task");
        Assert.HasCount(1, snapshot.VariableStates, "the input scope is removed on completion");
    }

    [TestMethod]
    public async Task ScriptTask_Outputs_AreTheOnlyVariablesThatPropagate()
    {
        var task = new ScriptTask("script1", "_context.a = 1; _context.b = 2")
        {
            OutputMappings = [new OutputMapping("=a + b", "sum")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-script-outputs", task), Vars(("count", 2L)));

        var root = RootScope(snapshot);
        Assert.AreEqual(3, Convert.ToInt32(await instance.GetVariable(root, "sum")));
        Assert.IsNull(await instance.GetVariable(root, "a"));
        Assert.IsNull(await instance.GetVariable(root, "b"));
    }

    [TestMethod]
    public async Task ScriptTask_WithoutOutputs_MergesWhatTheScriptSet_ButNotItsInputs()
    {
        var task = new ScriptTask("script1", "_context.a = _context.scaled + 1")
        {
            InputMappings = [new InputMapping("=count * 10", "scaled")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-script-passthrough", task), Vars(("count", 2L)));

        var root = RootScope(snapshot);
        Assert.AreEqual(21L, Convert.ToInt64(await instance.GetVariable(root, "a")));
        Assert.AreEqual(2L, Convert.ToInt64(await instance.GetVariable(root, "count")));
        Assert.IsNull(await instance.GetVariable(root, "scaled"));
    }

    [TestMethod]
    public async Task ScriptTask_InputThatDoesNotEvaluate_FailsTheTask()
    {
        var task = new ScriptTask("script1", "_context.ran = true")
        {
            InputMappings = [new InputMapping("=count +", "scaled")]
        };
        var instance = await Start(Linear("io-script-input-error", task), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        var failed = snapshot.CompletedActivities.Single(a => a.ActivityId == "script1");
        Assert.IsNotNull(failed.ErrorState);
        StringAssert.Contains(failed.ErrorState.Message, "Input mapping '=count +' -> 'scaled' on activity 'script1'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "ran"), "the script never ran");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task ScriptTask_OutputThatDoesNotEvaluate_FailsTheTask()
    {
        var task = new ScriptTask("script1", "_context.a = 1")
        {
            OutputMappings = [new OutputMapping("=a.Missing * 2", "x")]
        };
        var instance = await Start(Linear("io-script-output-error", task), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        var failed = snapshot.CompletedActivities.Single(a => a.ActivityId == "script1");
        StringAssert.Contains(failed.ErrorState!.Message, "Output mapping '=a.Missing * 2' -> 'x' on activity 'script1'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "a"), "a failed task merges nothing");
    }

    // --- Plain task (completed externally) ---

    [TestMethod]
    public async Task Task_OutputsMapTheResult_AndDropTheRest()
    {
        var task = new TaskActivity("task1")
        {
            InputMappings = [new InputMapping("=count * 2", "doubled")],
            OutputMappings = [new OutputMapping("=doubled + result", "total")]
        };
        var instance = await Start(Linear("io-task", task), Vars(("count", 2L)));

        await instance.CompleteActivity("task1", Vars(("result", 10L), ("noise", "x")));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        var root = RootScope(snapshot);
        Assert.AreEqual(14L, Convert.ToInt64(await instance.GetVariable(root, "total")));
        Assert.IsNull(await instance.GetVariable(root, "noise"));
        Assert.IsNull(await instance.GetVariable(root, "result"));
    }

    [TestMethod]
    public async Task Task_WithoutOutputs_MergesTheWholeResult()
    {
        var instance = await Start(Linear("io-task-passthrough", new TaskActivity("task1")), Vars(("count", 2L)));

        await instance.CompleteActivity("task1", Vars(("result", 10L)));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        Assert.AreEqual(10L, Convert.ToInt64(await instance.GetVariable(RootScope(snapshot), "result")));
    }

    [TestMethod]
    public async Task Task_OutputThatDoesNotEvaluate_FailsTheTask()
    {
        var task = new TaskActivity("task1") { OutputMappings = [new OutputMapping("=result.Missing * 2", "x")] };
        var instance = await Start(Linear("io-task-error", task), Vars(("count", 2L)));

        await instance.CompleteActivity("task1", Vars(("result", 10L)));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "task1", "Output mapping '=result.Missing * 2' -> 'x' on activity 'task1'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "result"), "a failed task merges nothing");
    }

    // --- User task (inputs survive a reactivation: they are journaled) ---

    [TestMethod]
    public async Task UserTask_InputsSurviveReactivation_AndOutputsFilterTheResult()
    {
        var task = new UserTask("review", "ada", [], [], null)
        {
            InputMappings = [new InputMapping("=order.Total * 2", "doubled")],
            OutputMappings = [new OutputMapping("=approved ? doubled : 0", "approvedTotal")]
        };
        // Deployed, so the reactivated instance finds its definition.
        var definitions = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("io-user-task");
        await definitions.DeployVersion(Linear("io-user-task", task), "<xml/>");
        var instance = await Start(await definitions.GetLatestDefinition(), Vars(("order", Vars(("Total", 50L)))));
        var active = (await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivities.Any(a => a.ActivityId == "review"))).ActiveActivities.Single(a => a.ActivityId == "review");

        await ForceAllGrainDeactivation(instance.GetPrimaryKey());
        await instance.ClaimUserTask(active.ActivityInstanceId, "ada", []);
        await instance.CompleteUserTask(active.ActivityInstanceId, "ada", Vars(("approved", true), ("comment", "ok")));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        var root = RootScope(snapshot);
        Assert.AreEqual(100L, Convert.ToInt64(await instance.GetVariable(root, "approvedTotal")));
        Assert.IsNull(await instance.GetVariable(root, "comment"));
        Assert.IsNull(await instance.GetVariable(root, "doubled"));
    }

    [TestMethod]
    public async Task UserTask_WithoutOutputs_MergesTheResult_ButNotItsInputs()
    {
        var task = new UserTask("review", "ada", [], [], null)
        {
            InputMappings = [new InputMapping("=count * 2", "doubled")]
        };
        var instance = await Start(Linear("io-user-task-passthrough", task), Vars(("count", 2L)));
        var active = await ActiveEntry(instance, "review");

        await instance.ClaimUserTask(active.ActivityInstanceId, "ada", []);
        await instance.CompleteUserTask(active.ActivityInstanceId, "ada", Vars(("approved", true)));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        var root = RootScope(snapshot);
        Assert.AreEqual(true, await instance.GetVariable(root, "approved"));
        Assert.IsNull(await instance.GetVariable(root, "doubled"), "an input stays local to the task");
    }

    [TestMethod]
    public async Task UserTask_InputThatDoesNotEvaluate_FailsTheTask()
    {
        var task = new UserTask("review", "ada", [], [], null)
        {
            InputMappings = [new InputMapping("=order.Total * 2", "doubled")] // no 'order'
        };
        var instance = await Start(Linear("io-user-task-input-error", task), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "review", "Input mapping '=order.Total * 2' -> 'doubled' on activity 'review'");
        Assert.IsFalse(snapshot.ActiveActivities.Any(a => a.ActivityId == "review"), "the task never became claimable");
    }

    [TestMethod]
    public async Task UserTask_OutputThatDoesNotEvaluate_FailsTheTask()
    {
        var task = new UserTask("review", "ada", [], [], null)
        {
            OutputMappings = [new OutputMapping("=approved.Missing * 2", "x")]
        };
        var instance = await Start(Linear("io-user-task-output-error", task), Vars(("count", 2L)));
        var active = await ActiveEntry(instance, "review");

        await instance.ClaimUserTask(active.ActivityInstanceId, "ada", []);
        await instance.CompleteUserTask(active.ActivityInstanceId, "ada", Vars(("approved", true)));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "review", "Output mapping '=approved.Missing * 2' -> 'x' on activity 'review'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "approved"));
    }

    // --- Custom (plugin) task ---

    [TestMethod]
    public async Task CustomTask_PluginReceivesResolvedInputs_AndOutputsMapItsResult()
    {
        var task = new CustomTaskActivity("ct1", "test-echo")
        {
            InputMappings = [new InputMapping("=order.Total + 1", "amount")],
            OutputMappings = [new OutputMapping("=received.amount", "charged")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-custom-task", task), Vars(("order", Vars(("Total", 41L)))));

        Assert.AreEqual(42L, Convert.ToInt64(TestEchoCustomTaskHandler.LastInputs!["amount"]));
        var root = RootScope(snapshot);
        Assert.AreEqual(42L, Convert.ToInt64(await instance.GetVariable(root, "charged")));
        Assert.IsNull(await instance.GetVariable(root, "status"), "unmapped result fields are dropped");
        Assert.IsNull(await instance.GetVariable(root, "amount"));
    }

    [TestMethod]
    public async Task CustomTask_WithoutOutputs_MergesThePluginResult()
    {
        var (instance, snapshot) = await RunToEnd(
            Linear("io-custom-task-passthrough", new CustomTaskActivity("ct1", "test-echo")), Vars(("count", 1L)));

        Assert.AreEqual("done", await instance.GetVariable(RootScope(snapshot), "status"));
    }

    [TestMethod]
    public async Task CustomTask_InputThatDoesNotEvaluate_FailsWithoutCallingThePlugin()
    {
        TestEchoCustomTaskHandler.LastInputs = null;
        var task = new CustomTaskActivity("ct1", "test-echo")
        {
            InputMappings = [new InputMapping("=order.Total * 2", "amount")] // no 'order'
        };
        var instance = await Start(Linear("io-custom-task-error", task), Vars(("count", 1L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        StringAssert.Contains(snapshot.CompletedActivities.Single(a => a.ActivityId == "ct1").ErrorState!.Message, "'amount'");
        Assert.IsNull(TestEchoCustomTaskHandler.LastInputs);
    }

    [TestMethod]
    public async Task CustomTask_OutputThatDoesNotEvaluate_FailsTheTask()
    {
        var task = new CustomTaskActivity("ct1", "test-echo")
        {
            OutputMappings = [new OutputMapping("=status.Missing * 2", "x")]
        };
        var instance = await Start(Linear("io-custom-task-output-error", task), Vars(("count", 1L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "ct1", "Output mapping '=status.Missing * 2' -> 'x' on activity 'ct1'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "status"), "a failed task merges nothing");
    }

    // --- Embedded sub-process ---

    [TestMethod]
    public async Task SubProcess_InputsAreVisibleInside_AndOutputsAreAllThatLeaves()
    {
        var inner = new ScriptTask("inner", "_context.innerResult = _context.limit * 2");
        var sub = SubProcessWith("sub", inner) with
        {
            InputMappings = [new InputMapping("=count + 1", "limit")],
            OutputMappings = [new OutputMapping("=innerResult", "subResult")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-subprocess", sub), Vars(("count", 2L)));

        var root = RootScope(snapshot);
        Assert.AreEqual(6L, Convert.ToInt64(await instance.GetVariable(root, "subResult")));
        Assert.IsNull(await instance.GetVariable(root, "innerResult"));
        Assert.IsNull(await instance.GetVariable(root, "limit"));
        Assert.HasCount(1, snapshot.VariableStates, "the sub-process's scopes are removed");
    }

    [TestMethod]
    public async Task SubProcess_WithoutOutputs_MergesItsScope_ButNotItsInputs()
    {
        var inner = new ScriptTask("inner", "_context.innerResult = _context.limit * 2");
        var sub = SubProcessWith("sub", inner) with { InputMappings = [new InputMapping("=count + 1", "limit")] };

        var (instance, snapshot) = await RunToEnd(Linear("io-subprocess-passthrough", sub), Vars(("count", 2L)));

        var root = RootScope(snapshot);
        Assert.AreEqual(6L, Convert.ToInt64(await instance.GetVariable(root, "innerResult")));
        Assert.IsNull(await instance.GetVariable(root, "limit"));
    }

    [TestMethod]
    public async Task SubProcess_OutputThatDoesNotEvaluate_FailsTheSubProcess()
    {
        var sub = SubProcessWith("sub", new ScriptTask("inner", "_context.x = 1")) with
        {
            OutputMappings = [new OutputMapping("=x +", "y")]
        };
        var instance = await Start(Linear("io-subprocess-error", sub), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        StringAssert.Contains(snapshot.CompletedActivities.Single(a => a.ActivityId == "sub").ErrorState!.Message, "on activity 'sub'");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task SubProcess_InputThatDoesNotEvaluate_FailsBeforeItsChildrenRun()
    {
        var sub = SubProcessWith("sub", new ScriptTask("inner", "_context.ran = true")) with
        {
            InputMappings = [new InputMapping("=nope.Value + 1", "limit")]
        };
        var instance = await Start(Linear("io-subprocess-input-error", sub), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "sub", "Input mapping '=nope.Value + 1' -> 'limit' on activity 'sub'");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "inner");
    }

    // --- Call activity ---

    [TestMethod]
    public async Task CallActivity_InputsReachTheChild_AndOutputsFilterItsResult()
    {
        var childStart = new StartEvent("childStart");
        var childScript = new ScriptTask("childScript", "_context.result = _context.amount * 3");
        var childEnd = new EndEvent("childEnd");
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("io-child").DeployVersion(new WorkflowDefinition
        {
            WorkflowId = "io-child",
            Activities = [childStart, childScript, childEnd],
            SequenceFlows = [new SequenceFlow("c1", childStart, childScript), new SequenceFlow("c2", childScript, childEnd)]
        }, "<xml/>");

        var call = new CallActivity("call1", "io-child", [], [])
        {
            InputMappings = [new InputMapping("=count * 5", "amount")],
            OutputMappings = [new OutputMapping("=result", "childResult")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-call-parent", call), Vars(("count", 2L)));

        var root = RootScope(snapshot);
        Assert.AreEqual(30L, Convert.ToInt64(await instance.GetVariable(root, "childResult")));
        Assert.IsNull(await instance.GetVariable(root, "result"));
        Assert.IsNull(await instance.GetVariable(root, "amount"));
    }

    [TestMethod]
    public async Task CallActivity_WithoutOutputs_MergesTheChildResult_ButNotItsInputs()
    {
        await DeployChild("io-child-passthrough");
        var call = new CallActivity("call1", "io-child-passthrough", [], [])
        {
            InputMappings = [new InputMapping("=count * 5", "amount")]
        };

        var (instance, snapshot) = await RunToEnd(Linear("io-call-passthrough", call), Vars(("count", 2L)));

        var root = RootScope(snapshot);
        Assert.AreEqual(30L, Convert.ToInt64(await instance.GetVariable(root, "result")));
        Assert.IsNull(await instance.GetVariable(root, "amount"), "an input stays local to the call activity");
    }

    [TestMethod]
    public async Task CallActivity_InputThatDoesNotEvaluate_FailsWithoutStartingTheChild()
    {
        await DeployChild("io-child-input-error");
        var call = new CallActivity("call1", "io-child-input-error", [], [])
        {
            InputMappings = [new InputMapping("=nope.Value * 5", "amount")]
        };
        var instance = await Start(Linear("io-call-input-error", call), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "call1", "Input mapping '=nope.Value * 5' -> 'amount' on activity 'call1'");
        Assert.IsNull(snapshot.CompletedActivities.Single(a => a.ActivityId == "call1").ChildWorkflowInstanceId,
            "no child workflow was started");
    }

    [TestMethod]
    public async Task CallActivity_OutputThatDoesNotEvaluate_FailsTheCallActivity()
    {
        await DeployChild("io-child-output-error");
        var call = new CallActivity("call1", "io-child-output-error", [], [])
        {
            InputMappings = [new InputMapping("=count", "amount")],
            OutputMappings = [new OutputMapping("=result.Missing * 2", "x")]
        };
        var instance = await Start(Linear("io-call-output-error", call), Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "call1", "Output mapping '=result.Missing * 2' -> 'x' on activity 'call1'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "result"));
    }

    // --- Events ---

    [TestMethod]
    public async Task StartEvent_OutputsMapTheStartVariables()
    {
        var start = new StartEvent("start") { OutputMappings = [new OutputMapping("=count + 1", "next")] };
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-start-event",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };

        var (instance, snapshot) = await RunToEnd(workflow, Vars(("count", 2L)));

        Assert.AreEqual(3L, Convert.ToInt64(await instance.GetVariable(RootScope(snapshot), "next")));
    }

    [TestMethod]
    public async Task MessageCatchEvent_OutputsMapTheMessagePayload()
    {
        var start = new StartEvent("start");
        var message = new MessageDefinition("msg1", "order-paid", "orderId");
        var catchEvent = new MessageIntermediateCatchEvent("waitPaid", "msg1")
        {
            OutputMappings = [new OutputMapping("=payment.Amount * 100", "cents")]
        };
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-message-catch",
            Activities = [start, catchEvent, end],
            SequenceFlows = [new SequenceFlow("f1", start, catchEvent), new SequenceFlow("f2", catchEvent, end)],
            Messages = [message]
        };
        var instance = await Start(workflow, Vars(("orderId", "o-1")));

        Assert.IsTrue(await DeliverMessageWhenSubscribed("order-paid", "o-1",
            Vars(("payment", Vars(("Amount", 12L))), ("noise", "x"))));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        var root = RootScope(snapshot);
        Assert.AreEqual(1200L, Convert.ToInt64(await instance.GetVariable(root, "cents")));
        Assert.IsNull(await instance.GetVariable(root, "noise"));
    }

    [TestMethod]
    public async Task BoundaryEvent_OutputsAreEvaluatedWhenItFires()
    {
        var start = new StartEvent("start");
        var task = new ScriptTask("risky", "FAIL()");
        var boundary = new BoundaryErrorEvent("onError", "risky", null)
        {
            OutputMappings = [new OutputMapping("=count * 3", "recovered")]
        };
        var end = new EndEvent("end");
        var recoveredEnd = new EndEvent("recoveredEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-boundary",
            Activities = [start, task, boundary, end, recoveredEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, recoveredEnd)
            ]
        };

        var (instance, snapshot) = await RunToEnd(workflow, Vars(("count", 2L)));

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "recoveredEnd");
        Assert.AreEqual(6L, Convert.ToInt64(await instance.GetVariable(RootScope(snapshot), "recovered")));
    }

    [TestMethod]
    public async Task StartEvent_WithoutOutputs_KeepsAllStartVariables()
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-start-event-passthrough",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };

        var (instance, snapshot) = await RunToEnd(workflow, Vars(("count", 2L), ("name", "ada")));

        var root = RootScope(snapshot);
        Assert.AreEqual(2L, Convert.ToInt64(await instance.GetVariable(root, "count")));
        Assert.AreEqual("ada", await instance.GetVariable(root, "name"));
    }

    [TestMethod]
    public async Task StartEvent_Outputs_AreAddedAlongsideTheStartVariables()
    {
        // The start variables are the process's own scope, not the event's result, so they stay.
        var start = new StartEvent("start") { OutputMappings = [new OutputMapping("=name.ToUpper()", "upper")] };
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-start-event-outputs",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };

        var (instance, snapshot) = await RunToEnd(workflow, Vars(("name", "ada")));

        Assert.AreEqual("ADA", await instance.GetVariable(RootScope(snapshot), "upper"));
    }

    [TestMethod]
    public async Task StartEvent_OutputThatDoesNotEvaluate_FailsTheInstance()
    {
        var start = new StartEvent("start") { OutputMappings = [new OutputMapping("=count +", "next")] };
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-start-event-error",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };
        var instance = await Start(workflow, Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "start", "Output mapping '=count +' -> 'next' on activity 'start'");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task MessageCatchEvent_WithoutOutputs_MergesTheWholePayload()
    {
        var instance = await Start(MessageCatchWorkflow("io-message-passthrough", []), Vars(("orderId", "o-1")));

        Assert.IsTrue(await DeliverMessageWhenSubscribed("order-paid", "o-1", Vars(("paid", true))));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        Assert.AreEqual(true, await instance.GetVariable(RootScope(snapshot), "paid"));
    }

    [TestMethod]
    public async Task MessageCatchEvent_OutputThatDoesNotEvaluate_FailsTheEvent()
    {
        var instance = await Start(MessageCatchWorkflow("io-message-error",
            [new OutputMapping("=payment.Amount * 100", "cents")]), Vars(("orderId", "o-1")));

        Assert.IsTrue(await DeliverMessageWhenSubscribed("order-paid", "o-1", Vars(("noise", "x")))); // no 'payment'
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "waitPaid", "Output mapping '=payment.Amount * 100' -> 'cents' on activity 'waitPaid'");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "noise"));
    }

    [TestMethod]
    public async Task MessageBoundaryEvent_OutputsFilterThePayload()
    {
        var instance = await Start(MessageBoundaryWorkflow("io-message-boundary",
            [new OutputMapping("=reason.ToUpper()", "cancelReason")]), Vars(("orderId", "o-2")));
        await ActiveEntry(instance, "work");

        Assert.IsTrue(await DeliverMessageWhenSubscribed("order-cancelled", "o-2", Vars(("reason", "late"), ("noise", "x"))));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        var root = RootScope(snapshot);
        CollectionAssert.Contains(snapshot.CompletedActivityIds, "cancelledEnd");
        Assert.AreEqual("LATE", await instance.GetVariable(root, "cancelReason"));
        Assert.IsNull(await instance.GetVariable(root, "noise"));
        Assert.IsNull(await instance.GetVariable(root, "reason"));
    }

    [TestMethod]
    public async Task MessageBoundaryEvent_WithoutOutputs_MergesTheWholePayload()
    {
        var instance = await Start(MessageBoundaryWorkflow("io-message-boundary-passthrough", []), Vars(("orderId", "o-3")));
        await ActiveEntry(instance, "work");

        Assert.IsTrue(await DeliverMessageWhenSubscribed("order-cancelled", "o-3", Vars(("reason", "late"))));
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "cancelledEnd");
        Assert.AreEqual("late", await instance.GetVariable(RootScope(snapshot), "reason"));
    }

    [TestMethod]
    public async Task BoundaryEvent_OutputThatDoesNotEvaluate_FailsTheBoundaryEvent()
    {
        var start = new StartEvent("start");
        var task = new ScriptTask("risky", "FAIL()");
        var boundary = new BoundaryErrorEvent("onError", "risky", null)
        {
            OutputMappings = [new OutputMapping("=nope.Value * 3", "recovered")]
        };
        var end = new EndEvent("end");
        var recoveredEnd = new EndEvent("recoveredEnd");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-boundary-error",
            Activities = [start, task, boundary, end, recoveredEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, task),
                new SequenceFlow("f2", task, end),
                new SequenceFlow("f3", boundary, recoveredEnd)
            ]
        };
        var instance = await Start(workflow, Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "onError", "Output mapping '=nope.Value * 3' -> 'recovered' on activity 'onError'");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "recoveredEnd");
    }

    [TestMethod]
    public async Task ThrowEvent_InputsStayLocal_AndTheEventCompletes()
    {
        var throwEvent = new SignalIntermediateThrowEvent("emit", "sig1")
        {
            InputMappings = [new InputMapping("=count + 1", "payloadCount")]
        };
        var workflow = Linear("io-throw-event", throwEvent) with { Signals = [new SignalDefinition("sig1", "io-shipped")] };

        var (instance, snapshot) = await RunToEnd(workflow, Vars(("count", 2L)));

        CollectionAssert.Contains(snapshot.CompletedActivityIds, "emit");
        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "payloadCount"), "an input stays local to the event");
        Assert.HasCount(1, snapshot.VariableStates, "the input scope is removed on completion");
    }

    [TestMethod]
    public async Task ThrowEvent_InputThatDoesNotEvaluate_FailsTheEvent()
    {
        var throwEvent = new SignalIntermediateThrowEvent("emit", "sig1")
        {
            InputMappings = [new InputMapping("=count +", "payloadCount")]
        };
        var workflow = Linear("io-throw-event-error", throwEvent) with { Signals = [new SignalDefinition("sig1", "io-shipped-err")] };
        var instance = await Start(workflow, Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        AssertFailedWith(snapshot, "emit", "Input mapping '=count +' -> 'payloadCount' on activity 'emit'");
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    [TestMethod]
    public async Task EndEvent_InputsStayLocal_AndTheInstanceCompletes()
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end") { InputMappings = [new InputMapping("=count + 1", "x")] };
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-end-event",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };

        var (instance, snapshot) = await RunToEnd(workflow, Vars(("count", 2L)));

        Assert.IsNull(await instance.GetVariable(RootScope(snapshot), "x"));
    }

    [TestMethod]
    public async Task EndEvent_InputThatDoesNotEvaluate_FailsTheEndEvent()
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end") { InputMappings = [new InputMapping("=nope.Value + 1", "x")] };
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "io-end-event-error",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };
        var instance = await Start(workflow, Vars(("count", 2L)));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        StringAssert.Contains(snapshot.CompletedActivities.Single(a => a.ActivityId == "end").ErrorState!.Message, "on activity 'end'");
    }

    // --- Multi-instance: each iteration maps on its own ---

    [TestMethod]
    public async Task MultiInstance_EachIterationMapsItsOwnInputs()
    {
        var inner = new ScriptTask("double", "_context.res = _context.twice")
        {
            InputMappings = [new InputMapping("=item * 2", "twice")]
        };
        var mi = new MultiInstanceActivity("double", inner,
            InputCollection: "items", InputDataItem: "item", OutputCollection: "results", OutputDataItem: "res");

        var (instance, snapshot) = await RunToEnd(Linear("io-multi-instance", mi),
            Vars(("items", new List<object> { 1L, 2L, 3L })));

        var results = (IEnumerable<object>)(await instance.GetVariable(RootScope(snapshot), "results"))!;
        CollectionAssert.AreEqual(new long[] { 2, 4, 6 }, results.Select(Convert.ToInt64).ToArray());
    }

    [TestMethod]
    public async Task MultiInstance_EachIterationFiltersItsOwnOutputs()
    {
        var inner = new ScriptTask("double", "_context.raw = _context.item * 2; _context.noise = 1")
        {
            OutputMappings = [new OutputMapping("=raw + 1", "res")]
        };
        var mi = new MultiInstanceActivity("double", inner,
            InputCollection: "items", InputDataItem: "item", OutputCollection: "results", OutputDataItem: "res");

        var (instance, snapshot) = await RunToEnd(Linear("io-multi-instance-outputs", mi),
            Vars(("items", new List<object> { 1L, 2L, 3L })));

        var root = RootScope(snapshot);
        var results = (IEnumerable<object>)(await instance.GetVariable(root, "results"))!;
        CollectionAssert.AreEqual(new long[] { 3, 5, 7 }, results.Select(Convert.ToInt64).ToArray());
        Assert.IsNull(await instance.GetVariable(root, "noise"));
        Assert.IsNull(await instance.GetVariable(root, "raw"));
    }

    [TestMethod]
    public async Task MultiInstance_IterationInputThatDoesNotEvaluate_FailsTheIteration()
    {
        var inner = new ScriptTask("double", "_context.res = _context.twice")
        {
            InputMappings = [new InputMapping("=item.Missing * 2", "twice")]
        };
        var mi = new MultiInstanceActivity("double", inner,
            InputCollection: "items", InputDataItem: "item", OutputCollection: "results", OutputDataItem: "res");
        var instance = await Start(Linear("io-multi-instance-error", mi), Vars(("items", new List<object> { 1L })));

        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsFailed);

        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "double"
            && a.ErrorState?.Message.Contains("Input mapping '=item.Missing * 2' -> 'twice' on activity 'double'") == true));
        CollectionAssert.DoesNotContain(snapshot.CompletedActivityIds, "end");
    }

    // --- helpers ---

    private static ExpandoObject Vars(params (string Key, object? Value)[] entries)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in entries)
            ((IDictionary<string, object?>)vars)[key] = value;
        return vars;
    }

    /// <summary>start → <paramref name="activity"/> → end.</summary>
    private static WorkflowDefinition Linear(string workflowId, Activity activity)
    {
        var start = new StartEvent("start");
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, activity, end],
            SequenceFlows = [new SequenceFlow("f1", start, activity), new SequenceFlow("f2", activity, end)]
        };
    }

    private static SubProcess SubProcessWith(string id, Activity inner)
    {
        var subStart = new StartEvent($"{id}Start");
        var subEnd = new EndEvent($"{id}End");
        return new SubProcess(id)
        {
            Activities = [subStart, inner, subEnd],
            SequenceFlows = [new SequenceFlow($"{id}1", subStart, inner), new SequenceFlow($"{id}2", inner, subEnd)]
        };
    }

    private async Task<IWorkflowInstanceGrain> Start(IWorkflowDefinition workflow, ExpandoObject variables)
    {
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: variables);
        return instance;
    }

    private async Task<(IWorkflowInstanceGrain, InstanceStateSnapshot)> RunToEnd(IWorkflowDefinition workflow, ExpandoObject variables)
    {
        var instance = await Start(workflow, variables);
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsFalse(snapshot.IsFailed, $"instance failed: {string.Join("; ", snapshot.CompletedActivities
            .Where(a => a.ErrorState is not null).Select(a => $"{a.ActivityId}: {a.ErrorState!.Message}"))}");
        return (instance, snapshot);
    }

    private static Guid RootScope(InstanceStateSnapshot snapshot)
        => snapshot.CompletedActivities.Single(a => a.ActivityId == "start").VariablesStateId;

    private static void AssertFailedWith(InstanceStateSnapshot snapshot, string activityId, string messageFragment)
    {
        var failed = snapshot.CompletedActivities.Single(a => a.ActivityId == activityId && a.ErrorState is not null);
        StringAssert.Contains(failed.ErrorState!.Message, messageFragment);
    }

    private async Task<ActivityInstanceSnapshot> ActiveEntry(IWorkflowInstanceGrain instance, string activityId)
        => (await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivities.Any(a => a.ActivityId == activityId)))
            .ActiveActivities.Single(a => a.ActivityId == activityId);

    /// <summary>Deploys childStart → childScript (<c>result = amount * 3</c>) → childEnd.</summary>
    private async Task DeployChild(string workflowId)
    {
        var childStart = new StartEvent("childStart");
        var childScript = new ScriptTask("childScript", "_context.result = _context.amount * 3");
        var childEnd = new EndEvent("childEnd");
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflowId).DeployVersion(new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [childStart, childScript, childEnd],
            SequenceFlows = [new SequenceFlow("c1", childStart, childScript), new SequenceFlow("c2", childScript, childEnd)]
        }, "<xml/>");
    }

    /// <summary>start → message catch <c>waitPaid</c> (order-paid, keyed by orderId) → end.</summary>
    private static WorkflowDefinition MessageCatchWorkflow(string workflowId, List<OutputMapping> outputs)
    {
        var start = new StartEvent("start");
        var catchEvent = new MessageIntermediateCatchEvent("waitPaid", "msg1") { OutputMappings = outputs };
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, catchEvent, end],
            SequenceFlows = [new SequenceFlow("f1", start, catchEvent), new SequenceFlow("f2", catchEvent, end)],
            Messages = [new MessageDefinition("msg1", "order-paid", "orderId")]
        };
    }

    /// <summary>start → task <c>work</c> → end, with an order-cancelled message boundary → cancelledEnd.</summary>
    private static WorkflowDefinition MessageBoundaryWorkflow(string workflowId, List<OutputMapping> outputs)
    {
        var start = new StartEvent("start");
        var work = new TaskActivity("work");
        var boundary = new MessageBoundaryEvent("onCancel", "work", "msgCancel") { OutputMappings = outputs };
        var end = new EndEvent("end");
        var cancelledEnd = new EndEvent("cancelledEnd");
        return new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, work, boundary, end, cancelledEnd],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, work),
                new SequenceFlow("f2", work, end),
                new SequenceFlow("f3", boundary, cancelledEnd)
            ],
            Messages = [new MessageDefinition("msgCancel", "order-cancelled", "orderId")]
        };
    }

    private sealed class CSharpSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IScriptExpressionExecutor, DynamicExpressoScriptExpressionExecutor>();
            services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();
        });
    }
}
