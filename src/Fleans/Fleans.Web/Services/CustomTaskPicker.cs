using Fleans.Application.CustomTasks;

namespace Fleans.Web.Services;

/// <summary>How the editor's plugin picker arranges the catalog (#1022).</summary>
public static class CustomTaskPicker
{
    /// <summary>One heading of the picker; a <c>null</c> category is the default group.</summary>
    public sealed record Group(string? Category, IReadOnlyList<CustomTaskCatalogEntry> Entries);

    /// <summary>Named categories in alphabetical order, then the default group.</summary>
    public static IReadOnlyList<Group> Arrange(IEnumerable<CustomTaskCatalogEntry> entries) =>
        entries
            .GroupBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key is null)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(
                g.Key,
                g.OrderBy(e => e.DisplayName ?? e.TaskType, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}
