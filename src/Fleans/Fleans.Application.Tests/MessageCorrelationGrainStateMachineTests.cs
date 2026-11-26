using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageCorrelationGrainStateMachineTests : WorkflowTestBase
{

    [TestMethod]
    public async Task Subscribe_OnSubscribed_ThrowsInvalidOperation()
    {
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/key2");
        await grain.Subscribe(Guid.NewGuid(), "activity1", Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid()));
    }

    [TestMethod]
    public async Task Unsubscribe_OnEmpty_IsNoOp()
    {
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/key3");

        // Should not throw
        await grain.Unsubscribe();
    }

    [TestMethod]
    public async Task DeliverMessage_OnEmpty_ReturnsFalse()
    {
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/key5");

        dynamic variables = new ExpandoObject();
        var result = await grain.DeliverMessage(variables);

        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task SubscribeUnsubscribeRoundTrip_AllowsResubscription()
    {
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/key6");

        await grain.Subscribe(Guid.NewGuid(), "activity1", Guid.NewGuid());
        await grain.Unsubscribe();
        await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid());
        await grain.Unsubscribe();

        // Final state is Empty — subscribe should succeed
        await grain.Subscribe(Guid.NewGuid(), "activity3", Guid.NewGuid());
    }

    [TestMethod]
    public async Task DeliverMessage_OnSubscribed_DeliversAndTransitionsToEmpty()
    {
        var messageStart = new MessageStartEvent("msgStart1", "msg1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end1");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sm-test-deliver-workflow",
            Activities = [messageStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", messageStart, task),
                new SequenceFlow("f2", task, end)
            ],
            Messages = [new MessageDefinition("msg1", "sm-test-deliver-msg", null)]
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sm-test-deliver-workflow");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        dynamic variables = new ExpandoObject();
        variables.orderId = "test-1";
        var instanceIds = await StartEvents.StartByMessage("sm-test-deliver-msg", variables);

        Assert.IsTrue(instanceIds.Count > 0);

        // After delivery, the correlation grain for this message should be empty.
        // Verify by trying to deliver again — should return false.
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(
            MessageCorrelationKey.Build("sm-test-deliver-msg", "test-1"));
        dynamic vars2 = new ExpandoObject();
        var result = await correlationGrain.DeliverMessage(vars2);
        Assert.IsFalse(result);
    }

    [TestMethod]
    public async Task DeliverMessage_ToAStaleSubscription_ReportsUndeliveredAndClearsIt()
    {
        // Arrange — an instance whose message catch already completed, and a leftover
        // subscription that still points at that catch (#847).
        var key = $"order-{Guid.NewGuid():N}";
        var start = new StartEvent("start");
        var waitPayment = new MessageIntermediateCatchEvent("waitPayment", "paymentDef");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sm-test-stale-subscription",
            Activities = [start, waitPayment, end],
            SequenceFlows = [new SequenceFlow("f1", start, waitPayment), new SequenceFlow("f2", waitPayment, end)],
            Messages = [new MessageDefinition("paymentDef", "stalePayment", "= orderId")]
        };
        dynamic vars = new ExpandoObject();
        vars.orderId = key;
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);
        Assert.IsTrue(await DeliverMessageWhenSubscribed("stalePayment", key));
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        var catchInstanceId = done.CompletedActivities.Single(a => a.ActivityId == "waitPayment").ActivityInstanceId;
        var correlation = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(
            MessageCorrelationKey.Build("stalePayment", key));
        await correlation.Subscribe(instance.GetPrimaryKey(), "waitPayment", catchInstanceId);

        // Act
        var delivered = await correlation.DeliverMessage(new ExpandoObject());

        // Assert — nothing consumed it, and the key is free again.
        Assert.IsFalse(delivered, "a message no instance consumed must not be reported delivered");
        await correlation.Subscribe(Guid.NewGuid(), "other", Guid.NewGuid());
    }

    [TestMethod]
    public async Task DeliverMessage_AfterAFailedAttempt_ReportsDeliveredEvenIfTheCatchIsAlreadyDone()
    {
        // Arrange — a delivery attempt fails (variables over the size limit throw), and by the
        // retry the catch is no longer waiting: the failed attempt may have applied the message,
        // so the retry must not report it undelivered and let it start something else (#847).
        var correlation = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>($"sm-test/retry-{Guid.NewGuid():N}");
        var instance = await StartedInstanceWithFinishedCatch();
        await correlation.Subscribe(instance.InstanceId, "waitPayment", instance.CatchInstanceId);
        var oversized = new ExpandoObject();
        ((IDictionary<string, object?>)oversized)["blob"] = new string('x', 2 * 1024 * 1024);
        await Assert.ThrowsAsync<Exception>(async () => await correlation.DeliverMessage(oversized));

        // Act
        var delivered = await correlation.DeliverMessage(new ExpandoObject());

        // Assert
        Assert.IsTrue(delivered, "a retry after a failed attempt counts as delivered");
        Assert.IsFalse(await correlation.DeliverMessage(new ExpandoObject()), "the subscription is gone");
    }

    [TestMethod]
    public async Task SendMessage_ToASubscriptionOfAPurgedInstance_GoesOnToMessageStartEvents()
    {
        // Arrange — a subscription left by an instance that no longer exists, and a process that
        // starts on the same message.
        var messageName = $"orderPaid-{Guid.NewGuid():N}";
        var start = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        var processKey = $"sm-test-fallthrough-{Guid.NewGuid():N}";
        await Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(processKey)
            .DeployVersion(new WorkflowDefinition
            {
                WorkflowId = processKey,
                Activities = [start, task, end],
                SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)],
                Messages = [new MessageDefinition("msg1", messageName, null)]
            }, "<xml/>");
        var correlation = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(
            MessageCorrelationKey.Build(messageName, "order-1"));
        await correlation.Subscribe(Guid.NewGuid(), "waitPayment", Guid.NewGuid());
        var commands = GetSiloService<IWorkflowCommandService>();

        // Act
        var result = await commands.SendMessage(messageName, "order-1", new ExpandoObject());

        // Assert — the stale subscription didn't count; the start event took the message.
        Assert.IsTrue(result.Delivered);
        Assert.HasCount(1, result.WorkflowInstanceIds!);
        await correlation.Subscribe(Guid.NewGuid(), "other", Guid.NewGuid());
    }

    private async Task<(Guid InstanceId, Guid CatchInstanceId)> StartedInstanceWithFinishedCatch()
    {
        var key = $"order-{Guid.NewGuid():N}";
        var start = new StartEvent("start");
        var waitPayment = new MessageIntermediateCatchEvent("waitPayment", "paymentDef");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sm-test-finished-catch",
            Activities = [start, waitPayment, end],
            SequenceFlows = [new SequenceFlow("f1", start, waitPayment), new SequenceFlow("f2", waitPayment, end)],
            Messages = [new MessageDefinition("paymentDef", "finishedCatchPayment", "= orderId")]
        };
        dynamic vars = new ExpandoObject();
        vars.orderId = key;
        var instance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await instance.StartWorkflow(workflow, variables: (ExpandoObject)vars);
        Assert.IsTrue(await DeliverMessageWhenSubscribed("finishedCatchPayment", key));
        var done = await WaitForCondition(instance.GetPrimaryKey(), s => s.IsCompleted);
        return (instance.GetPrimaryKey(), done.CompletedActivities.Single(a => a.ActivityId == "waitPayment").ActivityInstanceId);
    }

    [TestMethod]
    public async Task Subscribe_RacingWithUnsubscribe_ConvergesToValidState()
    {
        for (int i = 0; i < 100; i++)
        {
            var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>($"sm-race/key-{i}");
            var instanceId = Guid.NewGuid();
            var hostId = Guid.NewGuid();

            await grain.Subscribe(instanceId, "activity1", hostId);

            // Race subscribe (new) and unsubscribe concurrently
            var subscribeTask = Task.Run(async () =>
            {
                try
                {
                    await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid());
                }
                catch (InvalidOperationException)
                {
                }
            });
            var unsubscribeTask = Task.Run(async () =>
            {
                await grain.Unsubscribe();
            });

            await Task.WhenAll(subscribeTask, unsubscribeTask);

            // Final state must be valid: either subscribed (subscribe won after unsubscribe)
            // or empty. We verify by attempting subscribe — if it succeeds, state was Empty.
            // If it throws, state was Subscribed. Both are valid.
            try
            {
                await grain.Subscribe(Guid.NewGuid(), "activity3", Guid.NewGuid());
                // State was Empty — valid
            }
            catch (InvalidOperationException)
            {
                // State was Subscribed — valid
            }
        }
    }

    [TestMethod]
    public async Task Activate_WithOldState_InfersSubscribedFromSubscription()
    {
        // Subscribe, then force deactivation and reactivation.
        // OnActivateAsync backward-compat: if Subscription != null and Status == Empty
        // (old serialized state), infer Subscribed.
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/backcompat");
        var instanceId = Guid.NewGuid();
        await grain.Subscribe(instanceId, "activity1", Guid.NewGuid());

        // Force deactivation of all grains
        await ForceAllGrainDeactivation();

        // After reactivation, the grain should still be in Subscribed state.
        // Observable: duplicate subscribe throws.
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid()));
    }
}
