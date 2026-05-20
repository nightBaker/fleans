namespace Fleans.E2E.Tests.ApiClient;

internal static class BpmnFixtureLoader
{
    public static string Load(string planFolder, string fileName)
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "manual", planFolder, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"BPMN fixture not found: {path}. " +
                $"Expected at tests/manual/{planFolder}/{fileName} relative to repo root.",
                path);
        }
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "manual")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root (no ancestor of " +
            $"'{AppContext.BaseDirectory}' contains a 'tests/manual' directory).");
    }
}
