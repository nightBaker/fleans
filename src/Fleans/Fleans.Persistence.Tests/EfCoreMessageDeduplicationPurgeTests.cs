using System.Data.Common;
using Fleans.Application;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orleans.Runtime;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreMessageDeduplicationPurgeTests : PersistenceTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiryCutoff = Now - MessageDeduplicationState.Window;

    private static EfCoreWorkflowInstancePurgeService Service(IDbContextFactory<FleanCommandDbContext> factory)
        => new(factory, Substitute.For<IEventStore>(), NullLogger<EfCoreWorkflowInstancePurgeService>.Instance,
            new FixedTime());

    private static async Task Seed(IPersistenceTestFixture fixture, string key, DateTimeOffset at,
        MessageDeduplicationStatus status = MessageDeduplicationStatus.Correlated)
    {
        var record = new MessageDeduplicationState { Key = key, ETag = Guid.NewGuid().ToString("N") };
        if (status == MessageDeduplicationStatus.Correlated)
            record.RecordCorrelated(at);
        else
        {
            var id = Guid.NewGuid();
            record.PlanStarts([new PlannedStart("orders", id)], at);
            if (status == MessageDeduplicationStatus.Started)
                record.CompleteStarts([id]);
        }
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        db.MessageDeduplications.Add(record);
        await db.SaveChangesAsync();
    }

    [TestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_RemovesExpiredRecordsIncludingTheBoundary_AndKeepsRecentOnes(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        await Seed(fixture, "old/correlated", ExpiryCutoff.AddHours(-1));
        await Seed(fixture, "old/started", ExpiryCutoff.AddHours(-1), MessageDeduplicationStatus.Started);
        await Seed(fixture, "old/starting", ExpiryCutoff.AddHours(-1), MessageDeduplicationStatus.Starting);
        await Seed(fixture, "boundary", ExpiryCutoff);
        // Use a delta representable by both providers (Postgres timestamps have microsecond precision).
        await Seed(fixture, "recent", ExpiryCutoff.AddMilliseconds(1));

        // No completed instances exist, and their cutoff is much older than dedup expiry.
        var result = await Service(fixture.CommandFactory).PurgeCompletedAsync(Now.AddDays(-90));

        Assert.AreEqual(new WorkflowInstancePurgeResult(0, false, 4), result);
        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        CollectionAssert.AreEqual(new[] { "recent" }, await db.MessageDeduplications.Select(r => r.Key).ToArrayAsync());
    }

    [TestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_IsBoundedAndReportsMoreEvenWithoutInstances(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        for (var i = 0; i < 5; i++)
            await Seed(fixture, $"expired-{i}", ExpiryCutoff.AddMinutes(i - 10));
        var service = Service(fixture.CommandFactory);

        var first = await service.PurgeCompletedAsync(Now.AddDays(-7), batchSize: 2, maxInstances: 3);
        var second = await service.PurgeCompletedAsync(Now.AddDays(-7), batchSize: 2, maxInstances: 3);
        var third = await service.PurgeCompletedAsync(Now.AddDays(-7), batchSize: 2, maxInstances: 3);

        Assert.AreEqual(new WorkflowInstancePurgeResult(0, true, 3), first);
        Assert.AreEqual(new WorkflowInstancePurgeResult(0, false, 2), second);
        Assert.AreEqual(new WorkflowInstancePurgeResult(0, false), third);
    }

    [TestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Purge_DoesNotDeleteAKeyRefreshedBetweenSelectionAndDeletion(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        await Seed(fixture, "reused", ExpiryCutoff.AddHours(-1));
        var refresh = new BeforeDelete(async () =>
        {
            await using var db = await fixture.CommandFactory.CreateDbContextAsync();
            await db.MessageDeduplications.Where(r => r.Key == "reused")
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReceivedAt, Now).SetProperty(r => r.ETag, "renewed"));
        });
        await using var template = await fixture.CommandFactory.CreateDbContextAsync();
        var options = new DbContextOptionsBuilder<FleanCommandDbContext>(
            (DbContextOptions<FleanCommandDbContext>)template.GetService<IDbContextOptions>())
            .AddInterceptors(refresh).Options;

        var result = await Service(new InterceptedFactory(options)).PurgeCompletedAsync(Now.AddDays(-7));

        Assert.IsTrue(refresh.Invoked, "The update must race the DELETE, after candidate selection.");
        Assert.AreEqual(new WorkflowInstancePurgeResult(0, false), result);
        await using var check = await fixture.CommandFactory.CreateDbContextAsync();
        var record = await check.MessageDeduplications.SingleAsync();
        Assert.AreEqual(Now, record.ReceivedAt);
        Assert.AreEqual("renewed", record.ETag);
    }

    [TestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task PurgedKey_CanBeWrittenAgainWithThePreviouslyLoadedGrainState(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        await Seed(fixture, "orders/reuse", ExpiryCutoff.AddHours(-1));
        var storage = new EfCoreMessageDeduplicationGrainStorage(fixture.CommandFactory);
        var id = GrainId.Create("messagededuplication", "orders/reuse");
        var state = new TestGrainState<MessageDeduplicationState> { State = new() };
        await storage.ReadStateAsync("state", id, state);
        var previousETag = state.ETag;

        await Service(fixture.CommandFactory).PurgeCompletedAsync(Now.AddDays(-7));
        state.State.RecordCorrelated(Now);
        await storage.WriteStateAsync("state", id, state);

        Assert.AreNotEqual(previousETag, state.ETag);
        var reloaded = new TestGrainState<MessageDeduplicationState> { State = new() };
        await storage.ReadStateAsync("state", id, reloaded);
        Assert.IsTrue(reloaded.RecordExists);
        Assert.IsTrue(reloaded.State.IsRecorded(Now));
        Assert.AreEqual(Now, reloaded.State.ReceivedAt);
    }

    [TestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task CancelledPurge_LeavesRecordsUntouched(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        await Seed(fixture, "expired", ExpiryCutoff.AddHours(-1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => Service(fixture.CommandFactory)
            .PurgeCompletedAsync(Now.AddDays(-7), cancellationToken: cancellation.Token));

        await using var db = await fixture.CommandFactory.CreateDbContextAsync();
        Assert.AreEqual(1, await db.MessageDeduplications.CountAsync());
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InterceptedFactory(DbContextOptions<FleanCommandDbContext> options)
        : IDbContextFactory<FleanCommandDbContext>
    {
        public FleanCommandDbContext CreateDbContext() => new(options);
    }

    private sealed class BeforeDelete(Func<Task> refresh) : DbCommandInterceptor
    {
        public bool Invoked { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Invoked && command.CommandText.Contains("DELETE", StringComparison.Ordinal)
                && command.CommandText.Contains("MessageDeduplications", StringComparison.Ordinal))
            {
                Invoked = true;
                await refresh();
            }
            return result;
        }
    }
}
