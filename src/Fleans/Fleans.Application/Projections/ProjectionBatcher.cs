using System.Threading.Channels;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Projections;

/// <summary>
/// The silo's collector of read-model projections (#861): every <see cref="WorkflowInstanceProjector"/>
/// on the silo hands its write here and awaits it, and the batcher writes many instances at once
/// through <see cref="IWorkflowStateProjection.WriteBatchAsync"/> — one upsert statement for the
/// running rows instead of a transaction per row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Batches.</b> A batch starts when a write slot is free and a projection is queued; it takes
/// what is queued and waits up to <see cref="ProjectorOptions.MaxBatchDelayMs"/> for more, until it
/// holds <see cref="ProjectorOptions.MaxBatchSize"/>. At most
/// <see cref="ProjectorOptions.MaxConcurrentBatches"/> are written at once.
/// </para>
/// <para>
/// <b>At-least-once.</b> <see cref="WriteAsync"/> completes only once its batch is durable, so the
/// projector acknowledges the stream notification after the write, as before. A failed batch fails
/// every write in it: each projector rethrows and its stream redelivers, and the version guard
/// makes the rows that did land a no-op the second time.
/// </para>
/// <para>
/// <b>Back-pressure.</b> The queue holds at most <c>MaxBatchSize × MaxConcurrentBatches</c>
/// projections; past that, <see cref="WriteAsync"/> waits to enqueue, which holds back the
/// projector's stream delivery instead of growing memory while the database is slow.
/// </para>
/// </remarks>
public sealed partial class ProjectionBatcher : IAsyncDisposable
{
    private readonly IWorkflowStateProjection _projection;
    private readonly ILogger<ProjectionBatcher> _logger;
    private readonly int _maxBatchSize;
    private readonly TimeSpan _maxDelay;
    private readonly Channel<Pending> _queue;
    private readonly SemaphoreSlim _slots;
    private readonly Task _loop;

    public ProjectionBatcher(
        IWorkflowStateProjection projection,
        IOptions<ProjectorOptions> options,
        ILogger<ProjectionBatcher> logger)
    {
        _projection = projection;
        _logger = logger;
        var o = options.Value;
        _maxBatchSize = o.MaxBatchSize;
        _maxDelay = TimeSpan.FromMilliseconds(o.MaxBatchDelayMs);
        _slots = new SemaphoreSlim(o.MaxConcurrentBatches, o.MaxConcurrentBatches);
        _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(o.MaxBatchSize * o.MaxConcurrentBatches)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // On the thread pool, not on the scheduler of the grain whose activation created the singleton.
        _loop = Task.Run(RunAsync);
    }

    /// <summary>
    /// Writes the projection of <paramref name="state"/> at <paramref name="version"/> with the
    /// silo's next batch; completes once that batch is durable and throws when it failed.
    /// </summary>
    public async Task WriteAsync(string grainId, WorkflowInstanceState state, int version)
    {
        var pending = new Pending(
            new ProjectionWrite(grainId, state, version),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        await _queue.Writer.WriteAsync(pending);
        await pending.Done.Task;
    }

    private async Task RunAsync()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync())
        {
            // A slot first: while every slot is busy the queue fills, and the next batch is full.
            await _slots.WaitAsync();
            var batch = await CollectAsync(reader);
            if (batch.Count == 0)
            {
                _slots.Release();
                continue;
            }
            _ = FlushAsync(batch);
        }
    }

    /// <summary>What is queued, then what arrives within the delay, up to the batch size.</summary>
    private async Task<List<Pending>> CollectAsync(ChannelReader<Pending> reader)
    {
        var batch = new List<Pending>(_maxBatchSize);
        using var delay = new CancellationTokenSource(_maxDelay);
        while (batch.Count < _maxBatchSize)
        {
            if (reader.TryRead(out var pending))
            {
                batch.Add(pending);
                continue;
            }
            try
            {
                if (!await reader.WaitToReadAsync(delay.Token))
                    break; // completed: the silo is shutting down
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return batch;
    }

    private async Task FlushAsync(List<Pending> batch)
    {
        try
        {
            await _projection.WriteBatchAsync(batch.Select(p => p.Write).ToList());
            LogBatchWritten(batch.Count);
            foreach (var pending in batch)
                pending.Done.TrySetResult();
        }
        catch (Exception ex)
        {
            LogBatchFailed(ex, batch.Count);
            foreach (var pending in batch)
                pending.Done.TrySetException(ex);
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Stops taking writes; what is queued is still written.</summary>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _loop;
    }

    private sealed record Pending(ProjectionWrite Write, TaskCompletionSource Done);

    [LoggerMessage(EventId = 4075, Level = LogLevel.Debug, Message = "Read-model batch of {Count} projections written")]
    private partial void LogBatchWritten(int count);

    [LoggerMessage(EventId = 4076, Level = LogLevel.Warning,
        Message = "Read-model batch of {Count} projections failed; every projection in it is redelivered")]
    private partial void LogBatchFailed(Exception exception, int count);
}
