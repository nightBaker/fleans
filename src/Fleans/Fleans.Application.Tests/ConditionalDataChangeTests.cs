using Fleans.Application.Conditions;
using Fleans.Application.Grains;
using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Infrastructure.Conditions;
using Fleans.Infrastructure.Scripts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// Conditional events react to data written anywhere in the instance (#784): a completion on
/// another parallel branch writes to that branch's scope copy, which the waiting watcher's own
/// scope never sees, yet the condition must still see the write. Conditions run on the real C#
/// evaluator.
/// </summary>
[TestClass]
public class ConditionalDataChangeTests : WorkflowTestBase
{
    protected override void ConfigureCluster(TestClusterBuilder builder)
        => builder.AddSiloBuilderConfigurator<CSharpSiloConfigurator>();

    [TestMethod]
    public async Task InterruptingBoundary_VariableWrittenOnAParallelBranch_Fires()
    {
        // Arrange — fork → hostTask [conditional boundary decision == "escalate"] | triggerTask
        var workflow = ForkWorkflow("cond-boundary-branch-write", out _);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: Decision("pending"));
        await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivityIds.Contains("hostTask") && s.ActiveActivityIds.Contains("triggerTask"));

        // Act
        dynamic vars = new ExpandoObject();
        vars.decision = "escalate";
        await instance.CompleteActivity("triggerTask", (ExpandoObject)vars);

        // Assert — the host is interrupted and the boundary path runs
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("escalated"));
        Assert.IsFalse(snapshot.ActiveActivityIds.Contains("hostTask"));
        Assert.IsTrue(snapshot.CompletedActivities.Any(a => a.ActivityId == "hostTask" && a.IsCancelled));
        Assert.IsFalse(snapshot.IsFailed);
    }

    [TestMethod]
    public async Task InterruptingBoundary_UnrelatedWriteOnAParallelBranch_DoesNotFire()
    {
        // Arrange
        var workflow = ForkWorkflow("cond-boundary-branch-other-write", out _);
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: Decision("pending"));
        await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivityIds.Contains("hostTask") && s.ActiveActivityIds.Contains("triggerTask"));

        // Act
        dynamic vars = new ExpandoObject();
        vars.decision = "approve";
        await instance.CompleteActivity("triggerTask", (ExpandoObject)vars);
        await WaitForCondition(instance.GetPrimaryKey(), s => s.CompletedActivityIds.Contains("triggerTask"));
        await instance.CompleteActivity("hostTask", new ExpandoObject());

        // Assert — the host completes normally
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted || s.IsFailed);
        Assert.IsTrue(done.IsCompleted);
        Assert.IsFalse(done.IsFailed);
        CollectionAssert.DoesNotContain(done.CompletedActivityIds.ToList(), "escalated");
    }

    [TestMethod]
    public async Task InterruptingBoundary_VariableDeliveredToANonInterruptingMessageBoundary_Fires()
    {
        // Arrange — hostTask [conditional boundary decision == "escalate"] [non-interrupting message boundary]
        var orderId = $"cond-msg-{Guid.NewGuid():N}";
        var start = new StartEvent("start");
        var hostTask = new TaskActivity("hostTask");
        var cond = new ConditionalBoundaryEvent("cond", "hostTask", "_context.decision == \"escalate\"", IsInterrupting: true);
        var update = new MessageBoundaryEvent("update", "hostTask", "msg1", IsInterrupting: false);
        var escalated = new TaskActivity("escalated");
        var end = new EndEvent("end");
        var endUpdate = new EndEvent("endUpdate");
        var endEscalated = new EndEvent("endEscalated");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-boundary-message-payload",
            Activities = [start, hostTask, cond, update, escalated, end, endUpdate, endEscalated],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, hostTask),
                new SequenceFlow("f2", hostTask, end),
                new SequenceFlow("f3", update, endUpdate),
                new SequenceFlow("f4", cond, escalated),
                new SequenceFlow("f5", escalated, endEscalated)
            ],
            Messages = [new MessageDefinition("msg1", "condUpdate", "orderId")]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        dynamic initial = new ExpandoObject();
        initial.orderId = orderId;
        initial.decision = "pending";
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)initial);
        await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("hostTask"));

        // Act
        dynamic payload = new ExpandoObject();
        payload.decision = "escalate";
        Assert.IsTrue(await DeliverMessageWhenSubscribed("condUpdate", orderId, (ExpandoObject)payload));

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("escalated"));
        Assert.IsFalse(snapshot.ActiveActivityIds.Contains("hostTask"));
        Assert.IsFalse(snapshot.IsFailed);
    }

    [TestMethod]
    public async Task IntermediateCatch_VariableWrittenOnAParallelBranch_Resumes()
    {
        // Arrange — fork → wait(conditional catch ready == true) → afterWait | setter
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var wait = new ConditionalIntermediateCatchEvent("wait", "_context.ready == true");
        var afterWait = new TaskActivity("afterWait");
        var setter = new TaskActivity("setter");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "cond-catch-branch-write",
            Activities = [start, fork, wait, afterWait, setter, end1, end2],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, wait),
                new SequenceFlow("f3", fork, setter),
                new SequenceFlow("f4", wait, afterWait),
                new SequenceFlow("f5", afterWait, end1),
                new SequenceFlow("f6", setter, end2)
            ]
        };
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        dynamic initial = new ExpandoObject();
        initial.ready = false;
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)initial);
        await WaitForCondition(instance.GetPrimaryKey(),
            s => s.ActiveActivityIds.Contains("wait") && s.ActiveActivityIds.Contains("setter"));

        // Act
        dynamic vars = new ExpandoObject();
        vars.ready = true;
        await instance.CompleteActivity("setter", (ExpandoObject)vars);

        // Assert
        var snapshot = await WaitForCondition(instance.GetPrimaryKey(), s => s.ActiveActivityIds.Contains("afterWait"));
        CollectionAssert.Contains(snapshot.CompletedActivityIds.ToList(), "wait");
        Assert.IsFalse(snapshot.IsFailed);
    }

    private static ExpandoObject Decision(string value)
    {
        dynamic vars = new ExpandoObject();
        vars.decision = value;
        return vars;
    }

    private static WorkflowDefinition ForkWorkflow(string id, out ConditionalBoundaryEvent boundary)
    {
        var start = new StartEvent("start");
        var fork = new ParallelGateway("fork", IsFork: true);
        var hostTask = new TaskActivity("hostTask");
        var triggerTask = new TaskActivity("triggerTask");
        boundary = new ConditionalBoundaryEvent("cond", "hostTask", "_context.decision == \"escalate\"", IsInterrupting: true);
        var escalated = new TaskActivity("escalated");
        var end1 = new EndEvent("end1");
        var end2 = new EndEvent("end2");
        var end3 = new EndEvent("end3");
        return new WorkflowDefinition
        {
            WorkflowId = id,
            Activities = [start, fork, hostTask, triggerTask, boundary, escalated, end1, end2, end3],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, fork),
                new SequenceFlow("f2", fork, hostTask),
                new SequenceFlow("f3", fork, triggerTask),
                new SequenceFlow("f4", hostTask, end1),
                new SequenceFlow("f5", triggerTask, end2),
                new SequenceFlow("f6", boundary, escalated),
                new SequenceFlow("f7", escalated, end3)
            ]
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
