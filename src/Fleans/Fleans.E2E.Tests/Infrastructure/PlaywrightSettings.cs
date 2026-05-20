namespace Fleans.E2E.Tests.Infrastructure;

internal static class PlaywrightSettings
{
    public static bool Headless =>
        !string.Equals(
            Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static string? Trace =>
        Environment.GetEnvironmentVariable("PLAYWRIGHT_TRACE");
}
