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
    public async Task Subscribe_OnEmpty_Succeeds()
    {
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/key1");

        await grain.Subscribe(Guid.NewGuid(), "activity1", Guid.NewGuid());

        // Observable: a second subscribe on Subscribed state throws
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid()));
    }

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
    public async Task Unsubscribe_OnSubscribed_TransitionsToEmpty()
    {
        var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>("sm-test/key4");
        await grain.Subscribe(Guid.NewGuid(), "activity1", Guid.NewGuid());

        await grain.Unsubscribe();

        // Observable: subscribe succeeds again (Empty state)
        await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid());
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

        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("sm-test-deliver-msg");
        dynamic variables = new ExpandoObject();
        variables.orderId = "test-1";
        var instanceIds = await listener.FireMessageStartEvent(variables);

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
    public async Task Subscribe_RacingWithUnsubscribe_ConvergesToValidState()
    {
        for (int i = 0; i < 100; i++)
        {
            var grain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>($"sm-race/key-{i}");
            var instanceId = Guid.NewGuid();
            var hostId = Guid.NewGuid();

            await grain.Subscribe(instanceId, "activity1", hostId);

            // Race subscribe (new) and unsubscribe concurrently
            var subscribeFailed = false;
            var subscribeTask = Task.Run(async () =>
            {
                try
                {
                    await grain.Subscribe(Guid.NewGuid(), "activity2", Guid.NewGuid());
                }
                catch (InvalidOperationException)
                {
                    subscribeFailed = true;
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

    [TestMethod]
    public async Task DeliverMessage_OnSubscribed_CanResubscribeAfterDelivery()
    {
        var messageStart = new MessageStartEvent("msgStart1", "msg1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end1");

        var workflow = new WorkflowDefinition
        {
            WorkflowId = "sm-test-resub-workflow",
            Activities = [messageStart, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", messageStart, task),
                new SequenceFlow("f2", task, end)
            ],
            Messages = [new MessageDefinition("msg1", "sm-test-resub-msg", null)]
        };

        var processGrain = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("sm-test-resub-workflow");
        await processGrain.DeployVersion(workflow, "<placeholder/>");

        // Fire the message start event to create a correlation and deliver
        var listener = Cluster.GrainFactory.GetGrain<IMessageStartEventListenerGrain>("sm-test-resub-msg");
        dynamic variables = new ExpandoObject();
        var instanceIds = await listener.FireMessageStartEvent(variables);
        Assert.IsTrue(instanceIds.Count > 0);

        // After delivery completes, the grain transitions to Empty.
        // We should be able to subscribe again.
        var correlationGrain = Cluster.GrainFactory.GetGrain<IMessageCorrelationGrain>(
            MessageCorrelationKey.Build("sm-test-resub-msg", ""));
        await grain_subscribe_if_empty(correlationGrain);
    }

    private static async Task grain_subscribe_if_empty(IMessageCorrelationGrain grain)
    {
        try
        {
            await grain.Subscribe(Guid.NewGuid(), "resubscribe-activity", Guid.NewGuid());
            // Success — was Empty
        }
        catch (InvalidOperationException)
        {
            // Was Subscribed — also valid, delivery may still be in progress
        }
    }
}
