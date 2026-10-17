using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Dynamic;

namespace Fleans.Application.Tests;

[TestClass]
public class EventStoreDurabilityTests : WorkflowTestBase
{
    /// <summary>
    /// #652 — proves the load-bearing durability invariant: every confirmed
    /// batch of domain events round-trips to the WorkflowEventBatches journal
    /// before the grain method returns. If any confirm were buffered in
    /// memory and not persisted, an ungraceful kill would lose those events
    /// and reactivation would replay an incomplete journal.
    ///
    /// Approach: drive the grain through several public methods. After each
    /// call, read the journaled event count by instance id via a separate
    /// IDbContextFactory&lt;FleanCommandDbContext&gt; (same shared in-memory
    /// SQLite the grain writes to). Three layered assertions cover three
    /// bug classes:
    ///   • strict monotonicity → catches "ConfirmEvents skipped the append"
    ///   • contiguous 1..N batch sequence → catches "ConfirmEvents skipped
    ///     a version" (pure count comparisons would miss)
    ///   • batches cover exactly N events → catches duplicate-version bugs
    ///
    /// Why this and not a silo-restart test: cycles 69 and 73 documented two
    /// orthogonal failures in the silo-restart framing
    /// (`TestCluster.KillSiloAsync` destroys membership; deleting the
    /// snapshot row then reactivating hangs 30s). The row-count check covers
    /// the actual load-bearing property without inviting those infrastructure
    /// failure modes.
    /// </summary>
    [TestMethod]
    public async Task GrainCommands_PersistEventsToJournal()
    {
        // Arrange — 3-task sequential workflow
        var start = new StartEvent("start");
        var task1 = new TaskActivity("task1");
        var task2 = new TaskActivity("task2");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "durability-invariant-workflow",
            Activities = [start, task1, task2, end],
            SequenceFlows =
            [
                new SequenceFlow("seq1", start, task1),
                new SequenceFlow("seq2", task1, task2),
                new SequenceFlow("seq3", task2, end),
            ]
        };

        var instanceId = Guid.NewGuid();
        var grain = Cluster.GrainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId);
        var dbContextFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();

        // A journal row holds one append; the instance's event count is its highest LastVersion.
        async Task<int> EventCount()
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            var query = db.WorkflowEventBatches.Where(b => b.InstanceId == instanceId);
            return await query.AnyAsync() ? await query.MaxAsync(b => b.LastVersion) : 0;
        }

        // Act + Assert — every grain method call that confirms events must
        // grow the row count. The exact per-call event counts are
        // implementation-defined; we lock in strict monotonicity, not
        // specific numbers.

        Assert.AreEqual(0, await EventCount(), "no events before any grain method");

        // SetWorkflow may or may not emit events (pure-setter is plausible);
        // tolerate both. The load-bearing assertions come next.
        await grain.SetWorkflow(workflow);
        int afterSet = await EventCount();
        Assert.IsTrue(afterSet >= 0,
            $"row count never negative (got {afterSet})");

        await grain.StartWorkflow();
        int afterStart = await EventCount();
        Assert.IsTrue(afterStart > afterSet,
            $"StartWorkflow must append events to the journal " +
            $"(afterSet={afterSet}, afterStart={afterStart})");

        await grain.CompleteActivity("task1", new ExpandoObject());
        int afterTask1 = await EventCount();
        Assert.IsTrue(afterTask1 > afterStart,
            $"CompleteActivity(task1) must append events " +
            $"(afterStart={afterStart}, afterTask1={afterTask1})");

        await grain.CompleteActivity("task2", new ExpandoObject());
        int afterTask2 = await EventCount();
        Assert.IsTrue(afterTask2 > afterTask1,
            $"CompleteActivity(task2) must append events " +
            $"(afterTask1={afterTask1}, afterTask2={afterTask2})");

        // The journal must form a contiguous 1..N version sequence
        // (`WorkflowInstance.ApplyUpdatesToStorage` starts each batch at the
        // pre-confirm version + 1; first event therefore has Version=1, and
        // a snapshot at version N covers exactly the first N events — #833).
        // A skipped version would still satisfy the count growth above, but
        // replay would later reconstruct an incomplete state.
        await using (var db = await dbContextFactory.CreateDbContextAsync())
        {
            var batches = await db.WorkflowEventBatches
                .Where(b => b.InstanceId == instanceId)
                .OrderBy(b => b.FirstVersion)
                .Select(b => new { b.FirstVersion, b.LastVersion })
                .ToListAsync();
            var expectedFirst = 1;
            foreach (var batch in batches)
            {
                Assert.AreEqual(expectedFirst, batch.FirstVersion,
                    $"batches must form a contiguous 1..N sequence; expected a batch starting at " +
                    $"{expectedFirst}, got {batch.FirstVersion}");
                Assert.IsTrue(batch.LastVersion >= batch.FirstVersion);
                expectedFirst = batch.LastVersion + 1;
            }
            Assert.AreEqual(afterTask2, expectedFirst - 1, "the batches must cover every event");
        }
    }
}
