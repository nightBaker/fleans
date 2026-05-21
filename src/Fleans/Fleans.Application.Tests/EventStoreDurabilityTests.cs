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
    /// batch of domain events round-trips to the WorkflowEvents journal
    /// before the grain method returns. If any confirm were buffered in
    /// memory and not persisted, an ungraceful kill would lose those events
    /// and reactivation would replay an incomplete journal.
    ///
    /// Approach: drive the grain through several public methods. After each
    /// call, count rows in WorkflowEvents by grain id via a separate
    /// IDbContextFactory&lt;FleanCommandDbContext&gt; (same shared in-memory
    /// SQLite the grain writes to). Three layered assertions cover three
    /// bug classes:
    ///   • strict monotonicity → catches "ConfirmEvents skipped the append"
    ///   • contiguous 1..N version sequence → catches "ConfirmEvents skipped
    ///     a version" (pure count comparisons would miss)
    ///   • max-version == row-count → catches duplicate-version bugs
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
        // grainKey format matches WorkflowInstance.cs:119/146/171:
        //     var grainId = this.GetPrimaryKey().ToString();
        var grainKey = instanceId.ToString();
        var dbContextFactory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();

        async Task<int> EventCount()
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            return await db.WorkflowEvents.CountAsync(e => e.GrainId == grainKey);
        }

        async Task<int> MaxVersion()
        {
            await using var db = await dbContextFactory.CreateDbContextAsync();
            var query = db.WorkflowEvents.Where(e => e.GrainId == grainKey);
            return await query.AnyAsync() ? await query.MaxAsync(e => e.Version) : 0;
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

        // The journal must form a contiguous 0..N-1 version sequence
        // (`WorkflowInstance.ApplyUpdatesToStorage` passes the pre-confirm
        // version as the starting version; first event therefore has
        // Version=0). A skipped version would still satisfy the count
        // growth above, but replay would later reconstruct an incomplete
        // state.
        await using (var db = await dbContextFactory.CreateDbContextAsync())
        {
            var versions = await db.WorkflowEvents
                .Where(e => e.GrainId == grainKey)
                .OrderBy(e => e.Version)
                .Select(e => e.Version)
                .ToListAsync();
            Assert.AreEqual(afterTask2, versions.Count,
                "row count must match version count");
            for (int i = 0; i < versions.Count; i++)
            {
                Assert.AreEqual(i, versions[i],
                    $"versions must form a contiguous 0..N-1 sequence; " +
                    $"gap at index {i}: expected {i}, got {versions[i]}");
            }
        }

        // Max version equals (row count - 1) for 0-indexed versions —
        // catches duplicate-version bugs that would slip past the
        // contiguous-sequence assertion (e.g., the grain confirms a version
        // twice and the second insert silently overwrites or duplicates).
        Assert.AreEqual(afterTask2 - 1, await MaxVersion());
    }
}
