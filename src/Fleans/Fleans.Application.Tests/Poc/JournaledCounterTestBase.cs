using Orleans.TestingHost;

namespace Fleans.Application.Tests.Poc;

public abstract class JournaledCounterTestBase
{
    protected TestCluster Cluster { get; private set; } = null!;

    [TestInitialize]
    public void BaseSetup()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<JournaledCounterSiloConfigurator>();
        Cluster = builder.Build();
        Cluster.Deploy();
    }

    [TestCleanup]
    public void BaseCleanup()
    {
        Cluster?.StopAllSilos();
    }

    private class JournaledCounterSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder hostBuilder) =>
            hostBuilder
                .AddLogStorageBasedLogConsistencyProviderAsDefault()
                .AddMemoryGrainStorageAsDefault();
    }
}
