using System.Text.RegularExpressions;

namespace Fleans.Domain;

/// <summary>
/// Custom-task plugin versions (#1051): semver strings, compared by semver rules. Build metadata
/// (<c>+sha</c>) is dropped, so it affects neither ordering nor matching. A worker without a version
/// is <c>null</c> here, and so is a task on <c>latest</c>.
/// </summary>
public static partial class CustomTaskVersions
{
    /// <summary>What a BPMN task or the editor writes to follow the newest connected version.</summary>
    public const string Latest = "latest";

    public const int MaxLength = 128;

    /// <summary>
    /// The order <c>latest</c> picks from: stable versions, newest first, then prereleases, then
    /// workers without a version. A connected <c>2.0.0-rc.1</c> doesn't outrank a stable <c>1.9.0</c>.
    /// </summary>
    public static IComparer<string?> NewestFirst { get; } = Comparer<string?>.Create(CompareNewestFirst);

    /// <summary>
    /// <paramref name="version"/> without build metadata; <c>false</c> when it is not semver.
    /// </summary>
    public static bool TryNormalize(string? version, out string normalized)
    {
        normalized = string.Empty;
        if (version is null || version.Length > MaxLength)
            return false;
        var match = Semver().Match(version);
        if (!match.Success)
            return false;
        normalized = match.Groups["core"].Value;
        return true;
    }

    /// <summary>
    /// The items a task on <paramref name="requested"/> may use: those of exactly that version, or of
    /// the first version in <see cref="NewestFirst"/> order when it is <c>null</c> (latest).
    /// </summary>
    public static List<T> SelectGroup<T>(IEnumerable<T> items, Func<T, string?> versionOf, string? requested)
    {
        var all = items.ToList();
        if (requested is null && all.Count > 0)
        {
            var newest = all.Select(versionOf).Order(NewestFirst).First();
            return all.Where(item => versionOf(item) == newest).ToList();
        }
        return all.Where(item => versionOf(item) == requested).ToList();
    }

    private static int CompareNewestFirst(string? left, string? right)
    {
        if (left == right) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var (leftCore, leftPrerelease) = Split(left);
        var (rightCore, rightPrerelease) = Split(right);
        if (leftPrerelease.Length == 0 != (rightPrerelease.Length == 0))
            return leftPrerelease.Length == 0 ? -1 : 1;

        var core = CompareCore(leftCore, rightCore);
        if (core != 0)
            return -core;
        return -ComparePrerelease(leftPrerelease, rightPrerelease);
    }

    private static (int[] Core, string[] Prerelease) Split(string version)
    {
        var dash = version.IndexOf('-');
        var core = dash < 0 ? version : version[..dash];
        var prerelease = dash < 0 ? [] : version[(dash + 1)..].Split('.');
        return (core.Split('.').Select(int.Parse).ToArray(), prerelease);
    }

    private static int CompareCore(int[] left, int[] right)
    {
        for (var i = 0; i < left.Length; i++)
        {
            var part = left[i].CompareTo(right[i]);
            if (part != 0)
                return part;
        }
        return 0;
    }

    // Semver 2.0 §11: numeric identifiers by value and below alphanumeric ones; more identifiers win a tie.
    private static int ComparePrerelease(string[] left, string[] right)
    {
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = left[i].All(char.IsAsciiDigit);
            var rightNumeric = right[i].All(char.IsAsciiDigit);
            int part;
            if (leftNumeric && rightNumeric)
                part = left[i].Length != right[i].Length
                    ? left[i].Length.CompareTo(right[i].Length)
                    : string.CompareOrdinal(left[i], right[i]);
            else if (leftNumeric != rightNumeric)
                part = leftNumeric ? -1 : 1;
            else
                part = string.CompareOrdinal(left[i], right[i]);

            if (part != 0)
                return part;
        }
        return left.Length.CompareTo(right.Length);
    }

    [GeneratedRegex(@"^(?<core>(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(-(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(\.(0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*)?)(\+[0-9a-zA-Z-]+(\.[0-9a-zA-Z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Semver();
}
