using Fleans.Domain.States;
using Fleans.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orleans.Runtime;

namespace Fleans.Persistence.Tests;

[TestClass]
public class EfCoreCustomTaskCatalogGrainStorageTests
{
    private SqliteConnection _connection = null!;
    private IDbContextFactory<FleanCommandDbContext> _dbContextFactory = null!;
    private EfCoreCustomTaskCatalogGrainStorage _storage = null!;
    private const string StateName = "state";

    [TestInitialize]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<FleanCommandDbContext>()
            .UseFleansSqlite(_connection)
            .Options;

        _dbContextFactory = new TestDbContextFactory(options);
        _storage = new EfCoreCustomTaskCatalogGrainStorage(_dbContextFactory);

        using var db = _dbContextFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup() => _connection.Dispose();

    private static GrainId NewGrainId() => GrainId.Create("customtaskcatalog", "0");

    private static TestGrainState<CustomTaskCatalogState> CreateGrainState(params CustomTaskCatalogRowState[] rows) =>
        new() { State = new CustomTaskCatalogState { Entries = rows.ToList() } };

    private static TestGrainState<CustomTaskCatalogState> CreateEmptyGrainState() =>
        new() { State = new CustomTaskCatalogState() };

    [TestMethod]
    public async Task ReadStateAsync_EmptyDatabase_ReturnsNoRecord()
    {
        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, NewGrainId(), read);

        Assert.IsFalse(read.RecordExists);
        Assert.HasCount(0, read.State.Entries);
    }

    [TestMethod]
    public async Task WriteAndRead_SingleEntry_RoundTripsAllFields()
    {
        var grainId = NewGrainId();
        var write = CreateGrainState(new CustomTaskCatalogRowState
        {
            TaskType = "rest-call",
            SiloName = "worker-A",
            DisplayName = "REST Caller",
            ParameterSchemaJson = """{"Parameters":[]}""",
        });

        await _storage.WriteStateAsync(StateName, grainId, write);
        Assert.IsNotNull(write.ETag);
        Assert.IsTrue(write.RecordExists);

        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, read);

        var entry = read.State.Entries.Single();
        Assert.AreEqual("rest-call", entry.TaskType);
        Assert.AreEqual("worker-A", entry.SiloName);
        Assert.AreEqual("REST Caller", entry.DisplayName);
        Assert.AreEqual("""{"Parameters":[]}""", entry.ParameterSchemaJson);
    }

    [TestMethod]
    public async Task WriteAndRead_NullSchemaJson_RoundTripsAsNull()
    {
        var grainId = NewGrainId();
        var write = CreateGrainState(new CustomTaskCatalogRowState
        {
            TaskType = "no-schema",
            SiloName = "worker-X",
            DisplayName = null,
            ParameterSchemaJson = null,
        });

        await _storage.WriteStateAsync(StateName, grainId, write);

        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, read);

        var entry = read.State.Entries.Single();
        Assert.IsNull(entry.DisplayName);
        Assert.IsNull(entry.ParameterSchemaJson);
    }

    [TestMethod]
    public async Task Write_UpsertSameKey_UpdatesNotDuplicates()
    {
        var grainId = NewGrainId();
        var initial = CreateGrainState(new CustomTaskCatalogRowState
        {
            TaskType = "rest-call",
            SiloName = "worker-A",
            DisplayName = "v1",
            ParameterSchemaJson = """{"Parameters":[]}""",
        });
        await _storage.WriteStateAsync(StateName, grainId, initial);

        // Same key, mutated mutable fields.
        initial.State.Entries[0].DisplayName = "v2";
        initial.State.Entries[0].ParameterSchemaJson = """{"Parameters":[{"Name":"x"}]}""";
        await _storage.WriteStateAsync(StateName, grainId, initial);

        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, read);

        Assert.HasCount(1, read.State.Entries);
        Assert.AreEqual("v2", read.State.Entries[0].DisplayName);
        Assert.AreEqual("""{"Parameters":[{"Name":"x"}]}""", read.State.Entries[0].ParameterSchemaJson);
    }

    [TestMethod]
    public async Task Write_RemovedEntry_DropsRow()
    {
        var grainId = NewGrainId();
        var initial = CreateGrainState(
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-A" },
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-B" });
        await _storage.WriteStateAsync(StateName, grainId, initial);

        // Drop one and re-write.
        initial.State.Entries.RemoveAll(e => e.SiloName == "worker-B");
        await _storage.WriteStateAsync(StateName, grainId, initial);

        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, read);

        Assert.HasCount(1, read.State.Entries);
        Assert.AreEqual("worker-A", read.State.Entries[0].SiloName);
    }

    [TestMethod]
    public async Task Write_MultiSilo_SameTaskType_RoundTripsBothRows()
    {
        var grainId = NewGrainId();
        var write = CreateGrainState(
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-A", DisplayName = "REST Caller" },
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-B", DisplayName = "REST Caller" });
        await _storage.WriteStateAsync(StateName, grainId, write);

        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, read);

        Assert.HasCount(2, read.State.Entries);
        var silos = read.State.Entries.Select(e => e.SiloName).OrderBy(s => s).ToList();
        CollectionAssert.AreEqual(new[] { "worker-A", "worker-B" }, silos);
    }

    [TestMethod]
    public async Task ClearStateAsync_DropsAllRows()
    {
        var grainId = NewGrainId();
        var write = CreateGrainState(
            new CustomTaskCatalogRowState { TaskType = "rest-call", SiloName = "worker-A" },
            new CustomTaskCatalogRowState { TaskType = "email", SiloName = "worker-X" });
        await _storage.WriteStateAsync(StateName, grainId, write);

        await _storage.ClearStateAsync(StateName, grainId, write);

        var read = CreateEmptyGrainState();
        await _storage.ReadStateAsync(StateName, grainId, read);

        Assert.IsFalse(read.RecordExists);
        Assert.HasCount(0, read.State.Entries);
    }
}
