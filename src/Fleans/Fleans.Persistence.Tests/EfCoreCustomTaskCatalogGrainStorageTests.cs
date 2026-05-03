using Fleans.Domain.States;
using Fleans.Persistence.Tests.Infrastructure;
using Orleans.Runtime;

namespace Fleans.Persistence.Tests;

[TestClass]
[TestCategory("Postgres")]
public class EfCoreCustomTaskCatalogGrainStorageTests : PersistenceTestBase
{
    private const string StateName = "state";

    private static GrainId NewGrainId() => GrainId.Create("customtaskcatalog", "0");

    private static TestGrainState<CustomTaskCatalogState> CreateGrainState(params CustomTaskCatalogRowState[] rows) =>
        new() { State = new CustomTaskCatalogState { Entries = rows.ToList() } };

    private static TestGrainState<CustomTaskCatalogState> CreateEmptyGrainState() =>
        new() { State = new CustomTaskCatalogState() };

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ReadStateAsync_EmptyDatabase_ReturnsNoRecord(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, NewGrainId(), read);

        Assert.IsFalse(read.RecordExists);
        Assert.HasCount(0, read.State.Entries);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndRead_SingleEntry_RoundTripsAllFields(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId();
        var write = CreateGrainState(new CustomTaskCatalogRowState
        {
            TaskType = "rest-call",
            SiloName = "worker-A",
            DisplayName = "REST Caller",
            ParameterSchemaJson = """{"Parameters":[]}""",
        });

        await storage.WriteStateAsync(StateName, grainId, write);
        Assert.IsNotNull(write.ETag);
        Assert.IsTrue(write.RecordExists);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, read);

        var entry = read.State.Entries.Single();
        Assert.AreEqual("rest-call", entry.TaskType);
        Assert.AreEqual("worker-A", entry.SiloName);
        Assert.AreEqual("REST Caller", entry.DisplayName);
        Assert.AreEqual("""{"Parameters":[]}""", entry.ParameterSchemaJson);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task WriteAndRead_NullSchemaJson_RoundTripsAsNull(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId();
        var write = CreateGrainState(new CustomTaskCatalogRowState
        {
            TaskType = "no-schema",
            SiloName = "worker-X",
            DisplayName = null,
            ParameterSchemaJson = null,
        });

        await storage.WriteStateAsync(StateName, grainId, write);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, read);

        var entry = read.State.Entries.Single();
        Assert.IsNull(entry.DisplayName);
        Assert.IsNull(entry.ParameterSchemaJson);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_UpsertSameKey_UpdatesNotDuplicates(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId();
        var initial = CreateGrainState(new CustomTaskCatalogRowState
        {
            TaskType = "rest-call",
            SiloName = "worker-A",
            DisplayName = "v1",
            ParameterSchemaJson = """{"Parameters":[]}""",
        });
        await storage.WriteStateAsync(StateName, grainId, initial);

        initial.State.Entries[0].DisplayName = "v2";
        initial.State.Entries[0].ParameterSchemaJson = """{"Parameters":[{"Name":"x"}]}""";
        await storage.WriteStateAsync(StateName, grainId, initial);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, read);

        Assert.HasCount(1, read.State.Entries);
        Assert.AreEqual("v2", read.State.Entries[0].DisplayName);
        Assert.AreEqual("""{"Parameters":[{"Name":"x"}]}""", read.State.Entries[0].ParameterSchemaJson);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_RemovedEntry_DropsRow(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId();
        var initial = CreateGrainState(
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-A" },
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-B" });
        await storage.WriteStateAsync(StateName, grainId, initial);

        initial.State.Entries.RemoveAll(e => e.SiloName == "worker-B");
        await storage.WriteStateAsync(StateName, grainId, initial);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, read);

        Assert.HasCount(1, read.State.Entries);
        Assert.AreEqual("worker-A", read.State.Entries[0].SiloName);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task Write_MultiSilo_SameTaskType_RoundTripsBothRows(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId();
        var write = CreateGrainState(
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-A", DisplayName = "REST Caller" },
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-B", DisplayName = "REST Caller" });
        await storage.WriteStateAsync(StateName, grainId, write);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, read);

        Assert.HasCount(2, read.State.Entries);
        var silos = read.State.Entries.Select(e => e.SiloName).OrderBy(s => s).ToList();
        CollectionAssert.AreEqual(new[] { "worker-A", "worker-B" }, silos);
    }

    [DataTestMethod]
    [DataRow(PersistenceProvider.Sqlite)]
    [DataRow(PersistenceProvider.Postgres)]
    public async Task ClearStateAsync_DropsAllRows(PersistenceProvider provider)
    {
        await using var fixture = await TestFixtureFactory.CreateAsync(provider);
        var storage = new EfCoreCustomTaskCatalogGrainStorage(fixture.CommandFactory);

        var grainId = NewGrainId();
        var write = CreateGrainState(
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-A" },
            new CustomTaskCatalogRowState { TaskType = "email", SiloName = "worker-X" });
        await storage.WriteStateAsync(StateName, grainId, write);

        await storage.ClearStateAsync(StateName, grainId, write);

        var read = CreateEmptyGrainState();
        await storage.ReadStateAsync(StateName, grainId, read);

        Assert.IsFalse(read.RecordExists);
        Assert.HasCount(0, read.State.Entries);
    }
}
