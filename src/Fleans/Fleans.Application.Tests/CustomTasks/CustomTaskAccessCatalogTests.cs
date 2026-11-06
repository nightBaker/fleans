using Fleans.Application.CustomTasks;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class CustomTaskAccessCatalogTests : WorkflowTestBase
{
    private ICustomTaskCatalogGrain Catalog => Cluster.GrainFactory.GetGrain<ICustomTaskCatalogGrain>(0);

    private static string UniqueType() => $"t-{Guid.NewGuid():N}";

    private static CustomTaskRegistration Private(string taskType, string host, params string[] allowed) =>
        new(taskType, null, null, host, "Payments", CustomTaskVisibility.Private, [.. allowed]);

    [TestMethod]
    public async Task Register_WithoutAccessMetadata_IsPublicAndUncategorized()
    {
        // Arrange
        var taskType = UniqueType();

        // Act
        await Catalog.RegisterWorker(new CustomTaskRegistration(taskType, null, null, "silo"));

        // Assert
        var entry = await Catalog.Get(taskType);
        Assert.IsNotNull(entry);
        Assert.IsNull(entry.Category);
        Assert.AreEqual(CustomTaskVisibility.Public, entry.Visibility);
        Assert.IsNull(entry.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task Register_Private_CarriesCategoryAndAllowList()
    {
        // Arrange
        var taskType = UniqueType();

        // Act
        await Catalog.RegisterWorker(Private(taskType, "silo", "b", " a "));

        // Assert
        var entry = (await Catalog.GetAll()).Single(e => e.TaskType == taskType);
        Assert.AreEqual("Payments", entry.Category);
        Assert.AreEqual(CustomTaskVisibility.Private, entry.Visibility);
        CollectionAssert.AreEqual(new[] { "a", "b" }, entry.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task GetDisallowedTaskTypes_NamesOnlyPrivateTypesThatProcessMayNotUse()
    {
        // Arrange
        var open = UniqueType();
        var restricted = UniqueType();
        var unknown = UniqueType();
        await Catalog.RegisterWorker(new CustomTaskRegistration(open, null, null, "silo"));
        await Catalog.RegisterWorker(Private(restricted, "silo", "allowed"));

        // Act
        var forOther = await Catalog.GetDisallowedTaskTypes("other", [open, restricted, unknown]);
        var forAllowed = await Catalog.GetDisallowedTaskTypes("allowed", [open, restricted, unknown]);

        // Assert
        CollectionAssert.AreEqual(new[] { restricted }, forOther);
        Assert.IsEmpty(forAllowed);
    }

    [TestMethod]
    public async Task DeclaredAllowList_FollowsTheHostUntilEditedThenStoredListWins()
    {
        // Arrange
        var taskType = UniqueType();
        await Catalog.RegisterWorker(Private(taskType, "silo", "first"));

        // Act + Assert — before an edit the host's declaration applies
        await Catalog.RegisterWorker(Private(taskType, "silo", "second"));
        CollectionAssert.AreEqual(new[] { "second" }, (await Catalog.Get(taskType))!.AllowedProcessKeys);

        // Act + Assert — the edit wins over later declarations
        var edited = await Catalog.SetAllowedProcesses(taskType, ["edited"]);
        CollectionAssert.AreEqual(new[] { "edited" }, edited.AllowedProcessKeys);
        await Catalog.RegisterWorker(Private(taskType, "silo", "third"));
        await Catalog.RegisterWorker(Private(taskType, "another-silo", "third"));
        CollectionAssert.AreEqual(new[] { "edited" }, (await Catalog.Get(taskType))!.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task SetAllowedProcesses_PublicOrUnknownType_Throws()
    {
        // Arrange
        var open = UniqueType();
        await Catalog.RegisterWorker(new CustomTaskRegistration(open, null, null, "silo"));

        // Act + Assert
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Catalog.SetAllowedProcesses(open, ["p"]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Catalog.SetAllowedProcesses(UniqueType(), ["p"]));
    }

    [TestMethod]
    public async Task SetAllowedProcesses_BlankKey_ThrowsAndKeepsTheList()
    {
        // Arrange
        var taskType = UniqueType();
        await Catalog.RegisterWorker(Private(taskType, "silo", "allowed"));

        // Act
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => Catalog.SetAllowedProcesses(taskType, [" "]));

        // Assert
        CollectionAssert.AreEqual(new[] { "allowed" }, (await Catalog.Get(taskType))!.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task HostGone_RestrictionStaysAndEntryIsListedWithoutHosts()
    {
        // Arrange
        var taskType = UniqueType();
        await Catalog.RegisterWorker(Private(taskType, "worker", "allowed"));

        // Act — its worker disconnects
        await Catalog.UnregisterWorker(taskType, "worker");

        // Assert
        CollectionAssert.AreEqual(new[] { taskType }, await Catalog.GetDisallowedTaskTypes("other", [taskType]));
        var entry = (await Catalog.GetAll()).Single(e => e.TaskType == taskType);
        Assert.IsEmpty(entry.SiloNames);
        Assert.AreEqual(CustomTaskVisibility.Private, entry.Visibility);
        Assert.IsFalse(entry.IsOfferedTo("allowed"));

        // Act + Assert — the allow-list can still be managed
        var edited = await Catalog.SetAllowedProcesses(taskType, ["other"]);
        CollectionAssert.AreEqual(new[] { "other" }, edited.AllowedProcessKeys);
    }

    [TestMethod]
    public async Task EveryHostDeclaresPublic_TypeBecomesPublic()
    {
        // Arrange
        var taskType = UniqueType();
        await Catalog.RegisterWorker(Private(taskType, "silo", "allowed"));

        // Act
        await Catalog.RegisterWorker(new CustomTaskRegistration(taskType, null, null, "silo"));

        // Assert
        var entry = await Catalog.Get(taskType);
        Assert.AreEqual(CustomTaskVisibility.Public, entry!.Visibility);
        Assert.IsEmpty(await Catalog.GetDisallowedTaskTypes("other", [taskType]));
    }

    [TestMethod]
    public async Task PublicHostBesidePrivateHost_StaysPrivate_UntilThePrivateHostLeaves()
    {
        // Arrange
        var taskType = UniqueType();
        await Catalog.RegisterWorker(Private(taskType, "old", "allowed"));

        // Act + Assert — a host declaring public does not open up a type another host keeps private
        await Catalog.RegisterWorker(new CustomTaskRegistration(taskType, null, null, "new"));
        Assert.AreEqual(CustomTaskVisibility.Private, (await Catalog.Get(taskType))!.Visibility);

        // Act + Assert — the rolling upgrade ends
        await Catalog.UnregisterWorker(taskType, "old");
        Assert.AreEqual(CustomTaskVisibility.Public, (await Catalog.Get(taskType))!.Visibility);
    }
}
