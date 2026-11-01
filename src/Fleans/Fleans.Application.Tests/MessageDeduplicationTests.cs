using Fleans.Application.Grains;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Dynamic;

namespace Fleans.Application.Tests;

/// <summary>
/// #743 — a message sent with an idempotency key happens once: a retry, a concurrent duplicate or a
/// retry after the dedup grain reactivated returns the first send's outcome and starts nothing.
/// Starts are counted from the launcher's "start event fired" log (9370), written before the send
/// returns, so a duplicate start cannot hide behind the asynchronous read model.
/// </summary>
[TestClass]
[DoNotParallelize] // CapturedLogEvents
public class MessageDeduplicationTests : WorkflowTestBase
{
    private const int StartFiredEventId = 9370;

    private IWorkflowCommandService CommandService => GetSiloService<IWorkflowCommandService>();

    [TestMethod]
    public async Task PurgedExpiredKey_StartsAFreshInstanceAndDeduplicatesItsRetry()
    {
        await DeployMessageStart("dedup-purged", "dedupPurgedMsg");
        var previousId = Guid.NewGuid();
        var expired = new MessageDeduplicationState
        {
            Key = MessageDeduplicationKey.Build("dedupPurgedMsg", "req-1"),
            ETag = "expired"
        };
        expired.PlanStarts([new PlannedStart("dedup-purged", previousId)], DateTimeOffset.UtcNow.AddDays(-2));
        expired.CompleteStarts([previousId]);
        var factory = GetSiloService<IDbContextFactory<FleanCommandDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.MessageDeduplications.Add(expired);
            await db.SaveChangesAsync();
        }
        var purge = new EfCoreWorkflowInstancePurgeService(factory,
            GetSiloService<Fleans.Domain.Events.IEventStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EfCoreWorkflowInstancePurgeService>.Instance);

        var purged = await purge.PurgeCompletedAsync(DateTimeOffset.UtcNow.AddDays(-7));
        var first = await CommandService.SendMessage("dedupPurgedMsg", null, Variables(), idempotencyKey: "req-1");
        var retry = await CommandService.SendMessage("dedupPurgedMsg", null, Variables(), idempotencyKey: "req-1");

        Assert.AreEqual(1, purged.PurgedMessageDeduplicationsCount);
        Assert.IsTrue(first.Delivered);
        Assert.HasCount(1, first.WorkflowInstanceIds!);
        Assert.AreNotEqual(previousId, first.WorkflowInstanceIds[0]);
        CollectionAssert.AreEqual(first.WorkflowInstanceIds, retry.WorkflowInstanceIds);
    }

    [TestMethod]
    public async Task SameKey_StartsOneInstance_AndTheRetryReturnsItsId()
    {
        // Arrange
        await DeployMessageStart("dedup-same", "dedupSameMsg");
        using var _ = CapturedLogEvents.Capture(StartFiredEventId);

        // Act
        var first = await CommandService.SendMessage("dedupSameMsg", null, Variables(), idempotencyKey: "req-1");
        var retry = await CommandService.SendMessage("dedupSameMsg", null, Variables(), idempotencyKey: "req-1");

        // Assert
        Assert.IsTrue(first.Delivered);
        Assert.HasCount(1, first.WorkflowInstanceIds!);
        Assert.IsTrue(retry.Delivered);
        CollectionAssert.AreEqual(first.WorkflowInstanceIds, retry.WorkflowInstanceIds);
        Assert.AreEqual(1, StartsOf("dedup-same"));
    }

    [TestMethod]
    public async Task DifferentKeys_StartTwoInstances()
    {
        // Arrange
        await DeployMessageStart("dedup-different", "dedupDifferentMsg");
        using var _ = CapturedLogEvents.Capture(StartFiredEventId);

        // Act
        var first = await CommandService.SendMessage("dedupDifferentMsg", null, Variables(), idempotencyKey: "req-1");
        var second = await CommandService.SendMessage("dedupDifferentMsg", null, Variables(), idempotencyKey: "req-2");

        // Assert
        Assert.AreNotEqual(first.WorkflowInstanceIds!.Single(), second.WorkflowInstanceIds!.Single());
        Assert.AreEqual(2, StartsOf("dedup-different"));
    }

    [TestMethod]
    public async Task SameKey_ForAnotherMessageName_IsAnotherSend()
    {
        // Arrange
        await DeployMessageStart("dedup-name-a", "dedupNameA");
        await DeployMessageStart("dedup-name-b", "dedupNameB");

        // Act
        var a = await CommandService.SendMessage("dedupNameA", null, Variables(), idempotencyKey: "req-1");
        var b = await CommandService.SendMessage("dedupNameB", null, Variables(), idempotencyKey: "req-1");

        // Assert
        Assert.HasCount(1, a.WorkflowInstanceIds!);
        Assert.HasCount(1, b.WorkflowInstanceIds!);
        Assert.AreNotEqual(a.WorkflowInstanceIds![0], b.WorkflowInstanceIds![0]);
    }

    [TestMethod]
    public async Task ConcurrentDuplicates_StartOneInstance()
    {
        // Arrange
        await DeployMessageStart("dedup-concurrent", "dedupConcurrentMsg");
        using var _ = CapturedLogEvents.Capture(StartFiredEventId);

        // Act
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            CommandService.SendMessage("dedupConcurrentMsg", null, Variables(), idempotencyKey: "req-1")));

        // Assert
        var instanceId = results[0].WorkflowInstanceIds!.Single();
        Assert.IsTrue(results.All(r => r.Delivered && r.WorkflowInstanceIds!.SequenceEqual([instanceId])));
        Assert.AreEqual(1, StartsOf("dedup-concurrent"));
    }

    [TestMethod]
    public async Task Retry_AfterTheGrainReactivates_ReturnsTheRecordedId()
    {
        // Arrange
        await DeployMessageStart("dedup-reactivate", "dedupReactivateMsg");
        using var _ = CapturedLogEvents.Capture(StartFiredEventId);
        var first = await CommandService.SendMessage("dedupReactivateMsg", null, Variables(), idempotencyKey: "req-1");

        // Act — every idle grain, the dedup grain included, deactivates; the retry reads its row back
        await ForceAllGrainDeactivation();
        var retry = await CommandService.SendMessage("dedupReactivateMsg", null, Variables(), idempotencyKey: "req-1");

        // Assert
        CollectionAssert.AreEqual(first.WorkflowInstanceIds, retry.WorkflowInstanceIds);
        Assert.AreEqual(1, StartsOf("dedup-reactivate"));
    }

    [TestMethod]
    public async Task Retry_AfterAnInterruptedSend_StartsThePlannedInstance()
    {
        // Arrange — a send that persisted its plan and stopped before recording the outcome
        await DeployMessageStart("dedup-resume", "dedupResumeMsg");
        var plannedId = Guid.NewGuid();
        var record = new MessageDeduplicationState
        {
            Key = MessageDeduplicationKey.Build("dedupResumeMsg", "req-1"),
            ETag = "interrupted"
        };
        record.PlanStarts([new PlannedStart("dedup-resume", plannedId)], DateTimeOffset.UtcNow);
        await using (var db = await GetSiloService<IDbContextFactory<FleanCommandDbContext>>().CreateDbContextAsync())
        {
            db.MessageDeduplications.Add(record);
            await db.SaveChangesAsync();
        }

        // Act
        var retry = await CommandService.SendMessage("dedupResumeMsg", null, Variables(), idempotencyKey: "req-1");
        var again = await CommandService.SendMessage("dedupResumeMsg", null, Variables(), idempotencyKey: "req-1");

        // Assert
        CollectionAssert.AreEqual(new[] { plannedId }, retry.WorkflowInstanceIds);
        CollectionAssert.AreEqual(new[] { plannedId }, again.WorkflowInstanceIds);
        var snapshot = await QueryService.GetStateSnapshot(plannedId);
        Assert.IsNotNull(snapshot);
        Assert.IsTrue(snapshot.IsStarted);
    }

    [TestMethod]
    public async Task Retry_AfterACorrelatedDelivery_DoesNotFallThroughToTheStartEvent()
    {
        // Arrange — one instance waits for "dedupPaid" under orderId; another process starts on it
        var paid = new MessageDefinition("msg1", "dedupPaid", "orderId");
        var start = new StartEvent("start");
        var waitPaid = new MessageIntermediateCatchEvent("waitPaid", "msg1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        await Deploy(new WorkflowDefinition
        {
            WorkflowId = "dedup-waiting",
            Activities = [start, waitPaid, task, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, waitPaid),
                new SequenceFlow("f2", waitPaid, task),
                new SequenceFlow("f3", task, end)
            ],
            Messages = [paid]
        });
        await DeployMessageStart("dedup-started-by-paid", "dedupPaid");
        dynamic order = new ExpandoObject();
        order.orderId = "order-1";
        var waitingId = await CommandService.StartWorkflow("dedup-waiting", (ExpandoObject)order);
        using var _ = CapturedLogEvents.Capture(StartFiredEventId);

        // Act — the first send is delivered and consumes the subscription; the retry finds none
        var first = await CommandService.SendMessage("dedupPaid", "order-1", Variables(), idempotencyKey: "pay-1");
        var retry = await CommandService.SendMessage("dedupPaid", "order-1", Variables(), idempotencyKey: "pay-1");

        // Assert
        Assert.IsTrue(first.Delivered);
        Assert.IsNull(first.WorkflowInstanceIds);
        Assert.IsTrue(retry.Delivered);
        Assert.IsNull(retry.WorkflowInstanceIds);
        Assert.AreEqual(0, StartsOf("dedup-started-by-paid"));
        var snapshot = await QueryService.GetStateSnapshot(waitingId);
        Assert.IsTrue(snapshot!.ActiveActivities.Any(a => a.ActivityId == "task1"));
    }

    [TestMethod]
    public async Task WithoutAKey_EverySendStarts()
    {
        // Arrange
        await DeployMessageStart("dedup-none", "dedupNoneMsg");
        using var _ = CapturedLogEvents.Capture(StartFiredEventId);

        // Act
        await CommandService.SendMessage("dedupNoneMsg", null, Variables());
        await CommandService.SendMessage("dedupNoneMsg", null, Variables());

        // Assert
        Assert.AreEqual(2, StartsOf("dedup-none"));
    }

    [TestMethod]
    public async Task ASendThatStartedNothing_RecordsNothing_SoALaterRetryCanStart()
    {
        // Arrange
        await DeployMessageStart("dedup-late", "dedupLateMsg");
        var process = Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>("dedup-late");
        await process.Disable();

        // Act — the only process is disabled: not delivered. The dedup grain is this silo's first
        // cache user, so the Enable below also checks that a cache first used inside a grain still
        // hears about changes.
        var before = await CommandService.SendMessage("dedupLateMsg", null, Variables(), idempotencyKey: "req-1");
        await process.Enable();
        var after = await CommandService.SendMessage("dedupLateMsg", null, Variables(), idempotencyKey: "req-1");

        // Assert
        Assert.IsFalse(before.Delivered);
        Assert.IsTrue(after.Delivered);
        Assert.HasCount(1, after.WorkflowInstanceIds!);
    }

    [TestMethod]
    public async Task BlankKey_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CommandService.SendMessage("dedupBlank", null, Variables(), idempotencyKey: " "));
    }

    private static int StartsOf(string processDefinitionKey) =>
        CapturedLogEvents.Entries(StartFiredEventId).Count(e => e.Message.Contains($"process {processDefinitionKey} "));

    /// <summary>message start → task (waits) → end.</summary>
    private Task DeployMessageStart(string workflowId, string messageName)
    {
        var start = new MessageStartEvent("msgStart", "msg1");
        var task = new TaskActivity("task1");
        var end = new EndEvent("end");
        return Deploy(new WorkflowDefinition
        {
            WorkflowId = workflowId,
            Activities = [start, task, end],
            SequenceFlows = [new SequenceFlow("f1", start, task), new SequenceFlow("f2", task, end)],
            Messages = [new MessageDefinition("msg1", messageName, null)]
        });
    }

    private Task Deploy(WorkflowDefinition workflow) =>
        Cluster.GrainFactory.GetGrain<IProcessDefinitionGrain>(workflow.WorkflowId).DeployVersion(workflow, "<xml/>");

    private static ExpandoObject Variables()
    {
        dynamic variables = new ExpandoObject();
        variables.amount = 42;
        return variables;
    }
}
