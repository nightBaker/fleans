using Microsoft.Extensions.Options;

namespace Fleans.Application.Projections;

/// <summary>
/// The silo's limit on journal reads by read-model projectors (#1011): at most
/// <see cref="ProjectorOptions.MaxConcurrentReplays"/> projectors read and replay a journal at the
/// same time. The rest wait here, which holds back their stream delivery.
/// </summary>
/// <remarks>
/// Without it a burst of completions (a signal to 100 000 waiting instances) started a journal read
/// per notification at once. Far more of them than the silo's journal connection pool holds waited
/// for a connection and failed after the pool's timeout. Each failure was logged and redelivered,
/// and the worker spent its CPU on that instead of on scripts. Keep the limit below the journal
/// pool size (<c>Maximum Pool Size</c> of <c>ConnectionStrings:fleans-journal</c>).
/// </remarks>
public sealed class ProjectionReplaySlots
{
    private readonly SemaphoreSlim _slots;

    public ProjectionReplaySlots(IOptions<ProjectorOptions> options)
    {
        var max = options.Value.MaxConcurrentReplays;
        _slots = new SemaphoreSlim(max, max);
    }

    /// <summary>Waits for a slot; dispose the result to free it.</summary>
    public async ValueTask<Slot> EnterAsync()
    {
        await _slots.WaitAsync();
        return new Slot(_slots);
    }

    public readonly struct Slot(SemaphoreSlim slots) : IDisposable
    {
        public void Dispose() => slots.Release();
    }
}
