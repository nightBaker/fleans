namespace Fleans.Application.CustomTasks;

/// <summary>Who may use a custom-task plugin.</summary>
public enum CustomTaskVisibility
{
    /// <summary>Usable in any process.</summary>
    Public,
    /// <summary>Usable only in the processes on the plugin's allow-list (by process definition key).</summary>
    Private,
}

/// <summary>Rules for a plugin's category and allow-list, shared by every registration path.</summary>
public static class CustomTaskAccessRules
{
    /// <summary>Length of the catalog's <c>Category</c> column.</summary>
    public const int MaxCategoryLength = 128;

    /// <summary>A blank category means none: the plugin belongs to the editor's default group.</summary>
    public static string? NormalizeCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : category.Trim();

    /// <summary>Trimmed, distinct and sorted, so two lists compare equal whatever their order.</summary>
    /// <exception cref="ArgumentException">A key is blank.</exception>
    public static List<string> NormalizeProcessKeys(IEnumerable<string>? processKeys)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var key in processKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("A process key in the allow-list is blank.", nameof(processKeys));
            keys.Add(key.Trim());
        }
        return [.. keys];
    }

    /// <summary>Rejects a registration that declares something it cannot mean.</summary>
    /// <exception cref="ArgumentException">The category is too long, an allowed key is blank, or a public plugin declares an allow-list.</exception>
    public static void ValidateRegistration(
        string taskType, string? category, CustomTaskVisibility visibility, IEnumerable<string>? allowedProcessKeys)
    {
        if (category?.Length > MaxCategoryLength)
            throw new ArgumentException($"The category of '{taskType}' is longer than {MaxCategoryLength} characters.", nameof(category));
        if (!Enum.IsDefined(visibility))
            throw new ArgumentException($"Unknown visibility '{visibility}' for '{taskType}'.", nameof(visibility));

        var keys = NormalizeProcessKeys(allowedProcessKeys);
        if (visibility == CustomTaskVisibility.Public && keys.Count > 0)
            throw new ArgumentException($"'{taskType}' is public, so it cannot declare allowed processes.", nameof(allowedProcessKeys));
    }
}
