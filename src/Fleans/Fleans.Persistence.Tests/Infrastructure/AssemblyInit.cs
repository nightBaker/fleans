namespace Fleans.Persistence.Tests.Infrastructure;

/// <summary>
/// Wires the assembly-level <c>[AssemblyCleanup]</c> hook for stopping the shared
/// PostgreSQL container if any test in the assembly started it.
/// </summary>
[TestClass]
public static class AssemblyInit
{
    [AssemblyCleanup]
    public static async Task CleanupAsync() => await PostgresContainerFixture.DisposeAsync();
}
