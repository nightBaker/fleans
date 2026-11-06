using Fleans.Application.CustomTasks;
using Fleans.Web.Services;

namespace Fleans.Web.Tests;

[TestClass]
public class CustomTaskPickerTests
{
    private static CustomTaskCatalogEntry Entry(string taskType, string? category, string? displayName = null) =>
        new(taskType, displayName, null, ["silo"], category);

    [TestMethod]
    public void Arrange_NamedCategoriesAlphabeticallyThenTheDefaultGroup()
    {
        // Act
        var groups = CustomTaskPicker.Arrange([
            Entry("none", null),
            Entry("sms", "Messaging"),
            Entry("card", "payments"),
            Entry("wire", "Payments", "A wire"),
            Entry("email", "Messaging"),
        ]);

        // Assert
        CollectionAssert.AreEqual(new[] { "Messaging", "payments", null }, groups.Select(g => g.Category).ToList());
        CollectionAssert.AreEqual(new[] { "email", "sms" }, groups[0].Entries.Select(e => e.TaskType).ToList());
        CollectionAssert.AreEqual(new[] { "wire", "card" }, groups[1].Entries.Select(e => e.TaskType).ToList(),
            "categories match ignoring case, entries sort by what the picker shows");
        CollectionAssert.AreEqual(new[] { "none" }, groups[2].Entries.Select(e => e.TaskType).ToList());
    }

    [TestMethod]
    public void Arrange_NothingCategorized_IsOneDefaultGroup()
    {
        // Act
        var groups = CustomTaskPicker.Arrange([Entry("b", null), Entry("a", null)]);

        // Assert
        Assert.HasCount(1, groups);
        Assert.IsNull(groups[0].Category);
    }
}
