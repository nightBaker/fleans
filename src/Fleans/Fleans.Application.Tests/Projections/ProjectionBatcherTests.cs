using System.Collections.Concurrent;
using Fleans.Application.Projections;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Tests.Projections;

[TestClass]
public class ProjectionBatcherTests
{
    /// <summary>Records each batch; can hold the next one until released, or fail it.</summary>
    private sealed class RecordingProjection : IWorkflowStateProjection
    {
        public ConcurrentQueue<IReadOnlyList<ProjectionWrite>> Batches { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public int FailuresLeft { get; set; }

        public Task<WorkflowInstanceRow?> ReadRowAsync(string grainId) => Task.FromResult<WorkflowInstanceRow?>(null);

        public Task WriteAsync(string grainId, WorkflowInstanceState state, int version) =>
            WriteBatchAsync([new ProjectionWrite(grainId, state, version)]);

        public async Task WriteBatchAsync(IReadOnlyList<ProjectionWrite> writes)
        {
            Batches.Enqueue(writes);
            if (Gate is { } gate)
            {
                Gate = null;
                await gate.Task;
            }
            if (FailuresLeft > 0)
            {
                FailuresLeft--;
                throw new TimeoutException("injected");
            }
        }
    }

    private static ProjectionBatcher Batcher(RecordingProjection projection, int maxBatchSize = 64, int maxBatchDelayMs = 20, int maxConcurrentBatches = 4) =>
        new(projection,
            Options.Create(new ProjectorOptions
            {
                MaxBatchSize = maxBatchSize,
                MaxBatchDelayMs = maxBatchDelayMs,
                MaxConcurrentBatches = maxConcurrentBatches,
            }),
            NullLogger<ProjectionBatcher>.Instance);

    private static Task Write(ProjectionBatcher batcher, int version = 1) =>
        batcher.WriteAsync(Guid.NewGuid().ToString(), new WorkflowInstanceState(), version);

    [TestMethod]
    public async Task ConcurrentWrites_ShareBatches_NoLargerThanMaxBatchSize()
    {
        // Arrange — the only write slot is held by a first batch, so the writes behind it queue up
        var gate = new TaskCompletionSource();
        var projection = new RecordingProjection { Gate = gate };
        await using var batcher = Batcher(projection, maxBatchSize: 8, maxConcurrentBatches: 1);
        var first = Write(batcher);
        while (projection.Batches.IsEmpty)
            await Task.Delay(5);

        // Act — 20 more writes (more than the queue holds: the rest wait to enqueue), then release
        var rest = Enumerable.Range(0, 20).Select(_ => Write(batcher)).ToList();
        await Task.Delay(50);
        var batchesWhileHeld = projection.Batches.Count;
        var completedWhileHeld = rest.Count(w => w.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(rest.Append(first));

        // Assert — the 20 went out in full batches once the slot was free
        var batches = projection.Batches.ToList();
        Assert.AreEqual(1, batchesWhileHeld);
        Assert.AreEqual(0, completedWhileHeld);
        Assert.AreEqual(21, batches.Sum(b => b.Count));
        CollectionAssert.AreEqual(new[] { 1, 8, 8, 4 }, batches.Select(b => b.Count).ToArray());
    }

    [TestMethod]
    public async Task FailedBatch_FailsEveryWriteInIt_AndLaterBatchesSucceed()
    {
        // Arrange — writes issued together land in one batch, which fails
        var projection = new RecordingProjection { FailuresLeft = 1 };
        await using var batcher = Batcher(projection, maxBatchDelayMs: 200, maxConcurrentBatches: 1);

        // Act
        var writes = Enumerable.Range(0, 5).Select(_ => Write(batcher)).ToList();
        var outcomes = await Task.WhenAll(writes.Select(async w =>
        {
            try { await w; return null; }
            catch (Exception ex) { return ex; }
        }));

        // Assert — every write of the failed batch throws; the next write lands
        Assert.HasCount(1, projection.Batches);
        Assert.IsTrue(outcomes.All(e => e is TimeoutException), "every write of a failed batch must fail");
        await Write(batcher);
        Assert.HasCount(2, projection.Batches);
    }

    [TestMethod]
    public async Task SingleWrite_IsWrittenOnceTheDelayRunsOut()
    {
        // Arrange
        var projection = new RecordingProjection();
        await using var batcher = Batcher(projection, maxBatchDelayMs: 20);

        // Act
        var write = Write(batcher);
        var finished = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5)));

        // Assert
        Assert.AreSame(write, finished, "a lone write must not wait for a full batch");
        Assert.HasCount(1, projection.Batches.Single());
    }

    [TestMethod]
    public async Task Writes_AreNotAcknowledgedBeforeTheirBatchIsDurable()
    {
        // Arrange — the batch is held in the database
        var gate = new TaskCompletionSource();
        var projection = new RecordingProjection { Gate = gate };
        await using var batcher = Batcher(projection, maxBatchDelayMs: 0);

        // Act
        var write = Write(batcher);
        while (projection.Batches.IsEmpty)
            await Task.Delay(5);
        await Task.Delay(50);
        var completedWhileHeld = write.IsCompleted;
        gate.SetResult();
        await write;

        // Assert
        Assert.IsFalse(completedWhileHeld, "a write completed before its batch was written");
    }
}
