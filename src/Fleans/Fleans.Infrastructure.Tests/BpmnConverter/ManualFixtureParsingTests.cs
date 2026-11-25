namespace Fleans.Infrastructure.Tests.BpmnConverter;

/// <summary>
/// Every <c>.bpmn</c> fixture under <c>tests/manual/</c> is something a person is told to deploy, so each one must
/// parse — except the fixtures that exist to be rejected. A fixture that stops parsing (a converter change, a
/// renamed extension attribute) fails here instead of in the middle of a manual run.
/// </summary>
[TestClass]
public class ManualFixtureParsingTests : BpmnConverterTestBase
{
    /// <summary>Fixtures that are invalid on purpose (their test plan expects a rejection or a lint error), relative to <c>tests/manual/</c>.</summary>
    private static readonly HashSet<string> ExpectedToBeRejected =
    [
        "70-api-read-endpoints-negative-paths/malformed.bpmn",
        "70-api-read-endpoints-negative-paths/no-process.bpmn",
        // An editor lint sample: it deliberately holds a call activity without a called process.
        "73-orbit-admin-ui/orbit-editor-sample.bpmn",
        // A message end event is rejected until it sends its message (#843).
        "51-end-event-variants/message-end.bpmn",
    ];

    public static IEnumerable<object[]> Fixtures() =>
        Directory.EnumerateFiles(ManualDirectory(), "*.bpmn", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ManualDirectory(), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .Select(relative => new object[] { relative });

    [TestMethod]
    public void TheManualFixturesAreFound()
    {
        Assert.IsGreaterThan(100, Fixtures().Count());
        foreach (var rejected in ExpectedToBeRejected)
            Assert.IsTrue(File.Exists(Path.Combine(ManualDirectory(), rejected)), $"{rejected} still exists");
    }

    [TestMethod]
    [DynamicData(nameof(Fixtures))]
    public async Task ManualFixture_ParsesUnlessItExistsToBeRejected(string fixture)
    {
        // Arrange
        await using var stream = File.OpenRead(Path.Combine(ManualDirectory(), fixture));

        // Act
        Exception? error = null;
        try
        {
            var workflow = await _converter.ConvertFromXmlAsync(stream);
            Assert.IsNotEmpty(workflow.Activities, $"{fixture} has activities");
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            error = ex;
        }

        // Assert
        if (ExpectedToBeRejected.Contains(fixture))
            Assert.IsNotNull(error, $"{fixture} exists to be rejected, but it parsed");
        else
            Assert.IsNull(error, $"{fixture} does not parse: {error?.GetType().Name}: {error?.Message}");
    }

    private static string ManualDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "manual");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("tests/manual not found above " + AppContext.BaseDirectory);
    }
}
