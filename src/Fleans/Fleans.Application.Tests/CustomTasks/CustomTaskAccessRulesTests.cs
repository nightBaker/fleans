using Fleans.Application.CustomTasks;
using Fleans.Application.Tests.CustomTasks.TestStubs;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class CustomTaskAccessRulesTests
{
    [TestMethod]
    public void NormalizeProcessKeys_TrimsDeduplicatesAndSorts()
    {
        // Act
        var keys = CustomTaskAccessRules.NormalizeProcessKeys([" b ", "a", "b"]);

        // Assert
        CollectionAssert.AreEqual(new[] { "a", "b" }, keys);
    }

    [TestMethod]
    public void NormalizeProcessKeys_BlankKey_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CustomTaskAccessRules.NormalizeProcessKeys(["a", "  "]));
    }

    [TestMethod]
    public void NormalizeCategory_Blank_MeansNoCategory()
    {
        Assert.IsNull(CustomTaskAccessRules.NormalizeCategory("   "));
        Assert.AreEqual("Payments", CustomTaskAccessRules.NormalizeCategory(" Payments "));
    }

    [TestMethod]
    public void ValidateRegistration_PublicWithAllowList_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CustomTaskAccessRules.ValidateRegistration(
            "t", null, CustomTaskVisibility.Public, ["some-process"]));
    }

    [TestMethod]
    public void ValidateRegistration_CategoryTooLong_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => CustomTaskAccessRules.ValidateRegistration(
            "t", new string('c', CustomTaskAccessRules.MaxCategoryLength + 1), CustomTaskVisibility.Public, null));
    }

    [TestMethod]
    public void ValidateRegistration_PrivateWithNoAllowList_IsValid()
    {
        CustomTaskAccessRules.ValidateRegistration("t", "Payments", CustomTaskVisibility.Private, null);
    }

    [TestMethod]
    public void IsUsableBy_PublicAnyProcess_PrivateOnlyTheAllowedOnes()
    {
        // Arrange
        var publicPlugin = new CustomTaskCatalogEntry("p", null, null, ["silo"]);
        var privatePlugin = new CustomTaskCatalogEntry("q", null, null, ["silo"], null, CustomTaskVisibility.Private, ["allowed"]);

        // Assert
        Assert.IsTrue(publicPlugin.IsUsableBy("any"));
        Assert.IsTrue(publicPlugin.IsUsableBy(null));
        Assert.IsTrue(privatePlugin.IsUsableBy("allowed"));
        Assert.IsFalse(privatePlugin.IsUsableBy("ALLOWED"), "process keys are case-sensitive");
        Assert.IsFalse(privatePlugin.IsUsableBy("other"));
        Assert.IsFalse(privatePlugin.IsUsableBy(null));
    }

    [TestMethod]
    public void IsOfferedTo_AllowedButNotHosted_IsNotOffered()
    {
        // Arrange — a private plugin whose hosts are gone stays in the catalog
        var entry = new CustomTaskCatalogEntry("q", null, null, [], null, CustomTaskVisibility.Private, ["allowed"]);

        // Assert
        Assert.IsTrue(entry.IsUsableBy("allowed"));
        Assert.IsFalse(entry.IsOfferedTo("allowed"));
    }

    [TestMethod]
    public void AddCustomTaskPlugin_PublicWithAllowList_ThrowsAtStartup()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act + Assert
        Assert.ThrowsExactly<ArgumentException>(() => services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerA>(
            "test-no-op-a", allowedProcessKeys: ["some-process"]));
    }

    [TestMethod]
    public void AddCustomTaskPlugin_DefaultsToPublicAndUncategorized()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddCustomTaskPlugin<TestNoOpCustomTaskHandlerA>("test-no-op-a");

        // Assert
        var descriptor = services
            .Select(d => d.ImplementationInstance)
            .OfType<CustomTaskPluginDescriptor>()
            .Single();
        Assert.IsNull(descriptor.Category);
        Assert.AreEqual(CustomTaskVisibility.Public, descriptor.Visibility);
        Assert.IsNull(descriptor.AllowedProcessKeys);
    }
}
