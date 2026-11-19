using Microsoft.Extensions.Options;

namespace Fleans.Application;

/// <summary>
/// Bound on BPMN timer deliveries, bound from <c>Fleans:Timers</c> (#1066).
/// </summary>
public sealed class TimerDeliveryOptions
{
    public const string SectionName = "Fleans:Timers";

    /// <summary>
    /// How many due timers one silo delivers to their workflow instances at a time
    /// (<c>Fleans:Timers:DeliveryConcurrency</c>, default 64). Orleans fires every due reminder at
    /// once; without a bound, a burst of timers above the engine's fire rate reactivated that many
    /// instances concurrently and the Core silos ran out of memory and were declared dead
    /// (tests/load <c>timers-1m</c>, 60 000 due within a minute). A tick that finds every slot busy
    /// is not delivered: the timer's reminder stays registered and its 1-minute period ticks again,
    /// so a burst above capacity turns into lag. Only the silos read it.
    /// </summary>
    public int DeliveryConcurrency { get; set; } = 64;
}

/// <summary>
/// The silo's timer delivery slots (<see cref="TimerDeliveryOptions.DeliveryConcurrency"/>), shared by
/// every <c>TimerCallbackGrain</c> activation on it. Never waits: a tick that cannot take a slot
/// returns at once — see <c>TimerCallbackGrain.ReceiveReminder</c> for why it must not wait.
/// </summary>
public sealed class TimerDeliveryLimiter
{
    private readonly SemaphoreSlim _slots;

    public TimerDeliveryLimiter(IOptions<TimerDeliveryOptions> options)
    {
        _slots = new SemaphoreSlim(options.Value.DeliveryConcurrency);
    }

    /// <summary>Takes a slot if one is free. A <c>true</c> result must be paired with <see cref="Exit"/>.</summary>
    public bool TryEnter() => _slots.Wait(0);

    public void Exit() => _slots.Release();
}
