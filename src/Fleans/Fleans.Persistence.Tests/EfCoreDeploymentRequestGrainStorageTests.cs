using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreDeploymentRequestGrainStorageTests : PersistenceTestBase
{
    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteThenRead_RoundTripsTheRequestAndItsApprovals(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreDeploymentRequestGrainStorage(fixture.CommandFactory);
        var id = Guid.NewGuid();
        var grainId = GrainId.Create("deploymentrequest", id.ToString());
        var at = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var write = new TestGrainState<DeploymentRequestState> { State = new DeploymentRequestState() };
        write.State.Submit(id, "order-process", "<definitions/>", "alice", "adds a timer", 2, true, at);
        await storage.WriteStateAsync("state", grainId, write);

        // Act — a second write updates the row (ETag carried over)
        write.State.Approve("bob", "ok", at.AddMinutes(5));
        await storage.WriteStateAsync("state", grainId, write);
        var read = new TestGrainState<DeploymentRequestState> { State = new DeploymentRequestState() };
        await storage.ReadStateAsync("state", grainId, read);

        // Assert
        Assert.IsTrue(read.RecordExists);
        Assert.AreEqual(id, read.State.Id);
        Assert.AreEqual("order-process", read.State.ProcessDefinitionKey);
        Assert.AreEqual("<definitions/>", read.State.BpmnXml);
        Assert.AreEqual(DeploymentRequestStatus.Pending, read.State.Status);
        Assert.IsTrue(read.State.DeployDisabled);
        Assert.AreEqual(at, read.State.RequestedAt);
        var approval = read.State.Approvals.Single();
        Assert.AreEqual("bob", approval.UserId);
        Assert.AreEqual("ok", approval.Comment);
        Assert.AreEqual(at.AddMinutes(5), approval.ApprovedAt);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_WithAStaleETag_Throws(PersistenceProvider provider)
    {
        // Arrange
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreDeploymentRequestGrainStorage(fixture.CommandFactory);
        var id = Guid.NewGuid();
        var grainId = GrainId.Create("deploymentrequest", id.ToString());
        var write = new TestGrainState<DeploymentRequestState> { State = new DeploymentRequestState() };
        write.State.Submit(id, "order-process", "<definitions/>", "alice", null, 1, false, DateTimeOffset.UtcNow);
        await storage.WriteStateAsync("state", grainId, write);
        var stale = new TestGrainState<DeploymentRequestState> { State = write.State, ETag = "stale" };

        // Act & Assert
        await Assert.ThrowsAsync<InconsistentStateException>(() => storage.WriteStateAsync("state", grainId, stale));
    }
}
