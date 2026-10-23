namespace Fleans.Persistence.Tests;

[TestClass]
public class FleansPersistenceOptionsValidatorTests
{
    private static readonly FleansPersistenceOptionsValidator Validator = new();

    [TestMethod]
    public void Defaults_AreValid()
    {
        Assert.IsTrue(Validator.Validate(null, new FleansPersistenceOptions()).Succeeded);
    }

    [TestMethod]
    public void Default_CitusShardCount_Is128()
    {
        Assert.AreEqual(128, new FleansPersistenceOptions().Journal.Citus.ShardCount);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(64001)]
    public void CitusShardCount_OutOfRange_Fails(int shardCount)
    {
        var options = new FleansPersistenceOptions();
        options.Journal.Citus.ShardCount = shardCount;

        var result = Validator.Validate(null, options);

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "Persistence:Journal:Citus:ShardCount");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(64000)]
    public void CitusShardCount_InRange_IsValid(int shardCount)
    {
        var options = new FleansPersistenceOptions();
        options.Journal.Citus.ShardCount = shardCount;

        Assert.IsTrue(Validator.Validate(null, options).Succeeded);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void SnapshotInterval_BelowOne_Fails(int interval)
    {
        var result = Validator.Validate(null, new FleansPersistenceOptions { SnapshotInterval = interval });

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "SnapshotInterval");
    }

    [TestMethod]
    [DataRow(1000, 1000)]
    [DataRow(2000, 1000)]
    public void SnapshotInterval_NotBelowMaxEventsPerLoad_Fails(int interval, int maxEventsPerLoad)
    {
        // An instance between two snapshots would need more events than one load may read.
        var result = Validator.Validate(null,
            new FleansPersistenceOptions { SnapshotInterval = interval, MaxEventsPerLoad = maxEventsPerLoad });

        Assert.IsTrue(result.Failed);
        StringAssert.Contains(result.FailureMessage, "MaxEventsPerLoad");
    }
}
