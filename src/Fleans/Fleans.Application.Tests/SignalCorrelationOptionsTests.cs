using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Tests;

/// <summary>
/// Signal subscriptions are spread over <c>Fleans:Signals:BucketCount</c> buckets by a stable
/// hash of the workflow instance id; the bucket is part of the stored row key.
/// </summary>
[TestClass]
public class SignalCorrelationOptionsTests
{
    private static SignalCorrelationOptions Resolve(string? bucketCount)
    {
        var settings = new Dictionary<string, string?>();
        if (bucketCount is not null)
            settings["Fleans:Signals:BucketCount"] = bucketCount;

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddApplication();

        return services.BuildServiceProvider().GetRequiredService<IOptions<SignalCorrelationOptions>>().Value;
    }

    [TestMethod]
    public void Default_Is64Buckets_AndRandomIdsReachAllOfThem()
    {
        var options = Resolve(null);

        var buckets = Enumerable.Range(0, 5_000).Select(_ => options.BucketFor(Guid.NewGuid())).ToHashSet();

        Assert.AreEqual(64, options.BucketCount);
        Assert.IsTrue(buckets.All(b => b is >= 0 and < 64), "Bucket out of range.");
        Assert.AreEqual(64, buckets.Count);
    }

    [TestMethod]
    public void ConfiguredBucketCount_BoundsTheBuckets()
    {
        var options = Resolve("3");

        var buckets = Enumerable.Range(0, 2_000).Select(_ => options.BucketFor(Guid.NewGuid())).ToHashSet();

        Assert.AreEqual(3, options.BucketCount);
        CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, buckets.ToArray());
    }

    // Pinned values: stored rows carry the bucket, so the hash must never change silently.
    [TestMethod]
    [DataRow("00000000-0000-0000-0000-000000000000", 5)]
    [DataRow("6f9619ff-8b86-d011-b42d-00c04fc964ff", 54)]
    public void BucketFor_IsStable(string instanceId, int expectedBucket)
    {
        Assert.AreEqual(expectedBucket, SignalCorrelationOptions.BucketFor(Guid.Parse(instanceId), 64));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    public void NonPositiveBucketCount_IsRejected(string value)
    {
        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => Resolve(value));

        StringAssert.Contains(ex.Message, "Fleans:Signals:BucketCount must be greater than 0.");
    }
}
