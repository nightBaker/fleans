using System.Text.RegularExpressions;

namespace Fleans.Application.Tests;

/// <summary>
/// Guards the EventId allocation in <c>docs/conventions/observability-eventids.md</c>:
/// alerts and log queries keyed on an EventId must hit exactly one event. Scans the
/// sources rather than reflecting over assemblies so every project (Web, Api, plugins)
/// is covered without this test project referencing all of them.
/// </summary>
[TestClass]
public class LoggerEventIdUniquenessTests
{
    private static readonly Regex LoggerMessagePattern = new(
        @"\[LoggerMessage\((?<args>.*?)\)\]\s*(?:\[[^\]]*\]\s*)*(?:(?:private|public|internal|static|partial)\s+)+\w+\s+(?<method>\w+)\(",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EventIdPattern = new(@"EventId\s*=\s*(?<id>\d+)", RegexOptions.Compiled);

    /// <summary>
    /// EventIds deliberately declared more than once. Each entry must be one event logged from
    /// several classes — add one only with a note in the EventId catalog.
    /// </summary>
    private static readonly Dictionary<int, string> SharedEventIds = new()
    {
        [11100] = "SSL without certificate paths — KafkaQueueAdapter, KafkaQueueAdapterReceiver, KafkaQueueAdapterFactory",
    };

    [TestMethod]
    public void EveryLoggerMessageEventId_IsUsedForOneEventOnly()
    {
        // Arrange
        var sourceRoot = Path.Combine(FindRepoRoot(), "src", "Fleans");
        var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

        // Act
        var events = new List<(int Id, string Method, string Location)>();
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in LoggerMessagePattern.Matches(source))
            {
                var args = match.Groups["args"].Value;
                var id = EventIdPattern.Match(args);
                if (!id.Success)
                    continue;
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                events.Add((
                    int.Parse(id.Groups["id"].Value),
                    match.Groups["method"].Value,
                    $"{Path.GetRelativePath(sourceRoot, file)}:{line}"));
            }
        }

        var collisions = events
            .GroupBy(e => e.Id)
            .Where(g => g.Count() > 1 && !SharedEventIds.ContainsKey(g.Key))
            .Select(g => $"{g.Key}: " + string.Join(", ", g.Select(e => $"{e.Method} ({e.Location})")))
            .ToList();

        // Assert
        Assert.IsTrue(events.Count > 100, $"Expected to find the [LoggerMessage] declarations, found {events.Count}.");
        Assert.AreEqual(0, collisions.Count,
            "EventIds reused for different events — allocate from docs/conventions/observability-eventids.md:\n"
            + string.Join("\n", collisions));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Fleans", "Fleans.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            $"Could not locate repo root (no ancestor of '{AppContext.BaseDirectory}' contains src/Fleans/Fleans.sln).");
    }
}
