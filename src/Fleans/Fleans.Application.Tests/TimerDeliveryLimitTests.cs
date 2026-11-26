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
    private const string RetryReminderName = "timer-callback-retry";

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
        var timerGrain = FailingTimerGrain();
        var limiters = Limiters().ToList();
        var freeBefore = limiters.Sum(CountFreeSlots);

        // Act — the first failure moves the retries to the retry reminder; a retry that fails throws.
        await timerGrain.AsReference<IRemindable>().ReceiveReminder(TimerReminderName, Tick());
        var failure = await Assert.ThrowsAsync<Exception>(
            () => timerGrain.AsReference<IRemindable>().ReceiveReminder(RetryReminderName, Tick()));

        // Assert — the failed deliveries gave their slots back.
        Assert.IsNotNull(failure);
        Assert.AreEqual(freeBefore, limiters.Sum(CountFreeSlots));
    }

    [TestMethod]
    public async Task A_timer_that_lagged_for_a_day_is_retried_when_its_first_delivery_fails()
    {
        // Arrange — the tick fell due two days ago (deferred all that time), and now fails once.
        var timerGrain = FailingTimerGrain();
        var lagged = new TickStatus(DateTime.UtcNow.AddDays(-2), TimeSpan.FromMinutes(1), DateTime.UtcNow);

        // Act
        await timerGrain.AsReference<IRemindable>().ReceiveReminder(TimerReminderName, lagged);

        // Assert — lag is not failure: the delivery keeps retrying under the retry reminder.
        Assert.IsNull(await ReadReminder(timerGrain, TimerReminderName));
        Assert.IsNotNull(await ReadReminder(timerGrain, RetryReminderName), "the failure is retried, not abandoned");
    }

    [TestMethod]
    public async Task A_delivery_failing_for_a_day_is_abandoned()
    {
        // Arrange — the retries started two days ago.
        var timerGrain = FailingTimerGrain();
        await timerGrain.AsReference<IRemindable>().ReceiveReminder(TimerReminderName, Tick());
        var failingForDays = new TickStatus(DateTime.UtcNow.AddDays(-2), TimeSpan.FromMinutes(1), DateTime.UtcNow);

        // Act
        await timerGrain.AsReference<IRemindable>().ReceiveReminder(RetryReminderName, failingForDays);

        // Assert — no reminder is left ticking.
        Assert.IsNull(await ReadReminder(timerGrain, TimerReminderName));
        Assert.IsNull(await ReadReminder(timerGrain, RetryReminderName));
    }

    private ITimerCallbackGrain FailingTimerGrain() =>
        Cluster.GrainFactory.GetGrain<ITimerCallbackGrain>(Guid.NewGuid(), $"{Guid.NewGuid()}:timer1");

    [TestMethod]
    public void The_limiter_admits_DeliveryConcurrency_deliveries_and_frees_a_slot_on_exit()
    {
        // Arrange
        var limiter = new TimerDeliveryLimiter(Options.Create(new TimerDeliveryOptions { DeliveryConcurrency = 2 }), TimeProvider.System);

        // Act & Assert
        Assert.IsTrue(limiter.TryEnter());
        Assert.IsTrue(limiter.TryEnter());
        Assert.IsFalse(limiter.TryEnter(), "a third concurrent delivery must not be admitted");
        limiter.Exit();
        Assert.IsTrue(limiter.TryEnter(), "an exited delivery frees its slot");
    }

    [TestMethod]
    public void The_saturation_warning_is_claimed_at_most_once_per_interval()
    {
        // Arrange
        var clock = new ManualTimeProvider();
        var limiter = new TimerDeliveryLimiter(Options.Create(new TimerDeliveryOptions()), clock);

        // Act & Assert
        Assert.IsTrue(limiter.TryClaimSaturationWarning(), "the first saturation is logged");
        Assert.IsFalse(limiter.TryClaimSaturationWarning(), "further deferrals within the interval are not");
        clock.Advance(TimerDeliveryLimiter.SaturationWarningInterval - TimeSpan.FromSeconds(1));
        Assert.IsFalse(limiter.TryClaimSaturationWarning(), "still within the interval");
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(limiter.TryClaimSaturationWarning(), "a saturation after the interval is logged again");
    }

    [TestMethod]
    public void DeliveryConcurrency_defaults_to_64_and_comes_from_configuration()
    {
        // Arrange
        using var defaults = BuildServices(new());
        using var configured = BuildServices(new() { ["Fleans:Timers:DeliveryConcurrency"] = "4" });

        // Act
        var defaultValue = defaults.GetRequiredService<IOptions<TimerDeliveryOptions>>().Value.DeliveryConcurrency;
        var configuredValue = configured.GetRequiredService<IOptions<TimerDeliveryOptions>>().Value.DeliveryConcurrency;

        // Assert
        Assert.AreEqual(64, defaultValue);
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

    private Task<ReminderEntry?> ReadTimerReminder(ITimerCallbackGrain timerGrain) =>
        ReadReminder(timerGrain, TimerReminderName);

    private async Task<ReminderEntry?> ReadReminder(ITimerCallbackGrain timerGrain, string name) =>
        await SiloServices(Cluster.Primary).GetRequiredService<IReminderTable>()
            .ReadRow(timerGrain.GetGrainId(), name);

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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }
}
