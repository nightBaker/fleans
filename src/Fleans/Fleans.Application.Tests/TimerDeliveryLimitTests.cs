using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace Fleans.Application.Tests;

/// <summary>
/// Bounded timer delivery (#1066): a silo delivers at most <see cref="TimerDeliveryOptions.DeliveryConcurrency"/>
/// due timers at a time. A reminder tick that finds every slot busy is skipped — the timer's reminder stays
/// registered and its period ticks again — instead of reactivating one more instance.
/// </summary>
[TestClass]
public class TimerDeliveryLimitTests : WorkflowTestBase
{
    private const string TimerReminderName = "timer-callback";

    [TestMethod]
    public async Task A_tick_without_a_free_slot_is_not_delivered_and_the_next_tick_delivers_it()
    {
        // Arrange — Start → Timer(PT30M) → End; the reminder will not fire on its own during the test.
        var (workflowInstance, timerGrain) = await StartWorkflowWaitingOnTimer();
        var heldSlots = HoldEveryDeliverySlot();

        // Act — a tick while every slot of every silo is busy.
        await timerGrain.AsReference<IRemindable>().ReceiveReminder(TimerReminderName, Tick());

        // Assert — not delivered: the instance still waits on the timer and the reminder stays for the next tick.
        var deferred = await workflowInstance.GetStateSnapshot();
        Assert.IsFalse(deferred.IsCompleted, "a tick without a free slot must not deliver the timer");
        Assert.IsTrue(deferred.ActiveActivities.Any(a => a.ActivityId == "timer1"), "the timer should still be waiting");
        Assert.IsNotNull(await ReadTimerReminder(timerGrain), "the reminder must stay registered so its period retries");

        // Act — the slots free up and the reminder ticks again.
        ReleaseSlots(heldSlots);
        await timerGrain.AsReference<IRemindable>().ReceiveReminder(TimerReminderName, Tick());

        // Assert — delivered once a slot is free; the one-shot reminder is gone.
        var delivered = await workflowInstance.GetStateSnapshot();
        Assert.IsTrue(delivered.IsCompleted, "the next tick with a free slot should deliver the timer");
        Assert.IsTrue(delivered.CompletedActivities.Any(a => a.ActivityId == "end"), "should complete via the end event");
        Assert.IsNull(await ReadTimerReminder(timerGrain), "a delivered one-shot timer unregisters its reminder");
    }

    [TestMethod]
    public async Task A_delivery_that_fails_frees_its_slot()
    {
        // Arrange — a timer grain whose workflow instance was never started, so HandleTimerFired throws.
        var timerGrain = Cluster.GrainFactory.GetGrain<ITimerCallbackGrain>(
            Guid.NewGuid(), $"{Guid.NewGuid()}:timer1");
        var limiters = Limiters().ToList();
        var freeBefore = limiters.Sum(CountFreeSlots);

        // Act
        var failure = await Assert.ThrowsAsync<Exception>(
            () => timerGrain.AsReference<IRemindable>().ReceiveReminder(TimerReminderName, Tick()));

        // Assert — the failed delivery gave its slot back; the reminder's next tick retries it.
        Assert.IsNotNull(failure);
        Assert.AreEqual(freeBefore, limiters.Sum(CountFreeSlots));
    }

    [TestMethod]
    public void The_limiter_admits_DeliveryConcurrency_deliveries_and_frees_a_slot_on_exit()
    {
        // Arrange
        var limiter = new TimerDeliveryLimiter(Options.Create(new TimerDeliveryOptions { DeliveryConcurrency = 2 }));

        // Act & Assert
        Assert.IsTrue(limiter.TryEnter());
        Assert.IsTrue(limiter.TryEnter());
        Assert.IsFalse(limiter.TryEnter(), "a third concurrent delivery must not be admitted");
        limiter.Exit();
        Assert.IsTrue(limiter.TryEnter(), "an exited delivery frees its slot");
    }

    [TestMethod]
    public void DeliveryConcurrency_defaults_to_32_and_comes_from_configuration()
    {
        // Arrange
        using var defaults = BuildServices(new());
        using var configured = BuildServices(new() { ["Fleans:Timers:DeliveryConcurrency"] = "4" });

        // Act
        var defaultValue = defaults.GetRequiredService<IOptions<TimerDeliveryOptions>>().Value.DeliveryConcurrency;
        var configuredValue = configured.GetRequiredService<IOptions<TimerDeliveryOptions>>().Value.DeliveryConcurrency;

        // Assert
        Assert.AreEqual(32, defaultValue);
        Assert.AreEqual(4, configuredValue);
    }

    [TestMethod]
    public void A_DeliveryConcurrency_below_one_is_rejected()
    {
        // Arrange
        using var sp = BuildServices(new() { ["Fleans:Timers:DeliveryConcurrency"] = "0" });

        // Act & Assert
        Assert.ThrowsExactly<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<TimerDeliveryOptions>>().Value);
    }

    private async Task<(IWorkflowInstanceGrain Instance, ITimerCallbackGrain TimerGrain)> StartWorkflowWaitingOnTimer()
    {
        var start = new StartEvent("start");
        var timer = new TimerIntermediateCatchEvent("timer1", new TimerDefinition(TimerType.Duration, "PT30M"));
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "timer-delivery-limit-test",
            Activities = [start, timer, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, timer),
                new SequenceFlow("f2", timer, end)
            ]
        };

        var workflowInstance = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(Guid.NewGuid());
        await workflowInstance.StartWorkflow(workflow);

        var snapshot = await workflowInstance.GetStateSnapshot();
        var timerInstanceId = snapshot.ActiveActivities.Single(a => a.ActivityId == "timer1").ActivityInstanceId;
        var timerGrain = Cluster.GrainFactory.GetGrain<ITimerCallbackGrain>(
            workflowInstance.GetPrimaryKey(), $"{timerInstanceId}:timer1");
        return (workflowInstance, timerGrain);
    }

    private async Task<ReminderEntry?> ReadTimerReminder(ITimerCallbackGrain timerGrain) =>
        await SiloServices(Cluster.Primary).GetRequiredService<IReminderTable>()
            .ReadRow(timerGrain.GetGrainId(), TimerReminderName);

    private static TickStatus Tick() => new(DateTime.UtcNow, TimeSpan.FromMinutes(1), DateTime.UtcNow);

    /// <summary>Takes every free slot of every silo (a timer grain can be placed on either).</summary>
    private List<(TimerDeliveryLimiter Limiter, int Held)> HoldEveryDeliverySlot() =>
        Limiters().Select(limiter =>
        {
            var held = 0;
            while (limiter.TryEnter())
                held++;
            return (limiter, held);
        }).ToList();

    private static void ReleaseSlots(List<(TimerDeliveryLimiter Limiter, int Held)> heldSlots)
    {
        foreach (var (limiter, held) in heldSlots)
            for (var i = 0; i < held; i++)
                limiter.Exit();
    }

    private static int CountFreeSlots(TimerDeliveryLimiter limiter)
    {
        var free = 0;
        while (limiter.TryEnter())
            free++;
        for (var i = 0; i < free; i++)
            limiter.Exit();
        return free;
    }

    private IEnumerable<TimerDeliveryLimiter> Limiters() =>
        Cluster.Silos.Select(silo => SiloServices(silo).GetRequiredService<TimerDeliveryLimiter>());

    private static IServiceProvider SiloServices(SiloHandle silo) => ((InProcessSiloHandle)silo).SiloHost.Services;

    private static ServiceProvider BuildServices(Dictionary<string, string?> config)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddApplication();
        return services.BuildServiceProvider();
    }
}
