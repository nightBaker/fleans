namespace Fleans.Persistence.Tests;

[TestClass]
public class AssemblyInit
{
    [AssemblyInitialize]
    public static async Task Initialize(TestContext _)
    {
        await PostgresContainerFixture.StartAsync();
    }

    [AssemblyCleanup]
    public static async Task Cleanup()
    {
        await PostgresContainerFixture.StopAsync();
    }
}
