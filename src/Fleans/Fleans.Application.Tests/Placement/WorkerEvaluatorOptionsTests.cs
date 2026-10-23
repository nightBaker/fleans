using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.Application.Tests.Placement;

/// <summary>
/// #857: callers spread script/condition evaluator calls over
/// <c>Fleans:Worker:EvaluatorKeyCount</c> keys, so each core silo reaches several worker silos.
/// </summary>
[TestClass]
public class WorkerEvaluatorOptionsTests
{
    private static WorkerEvaluatorOptions Resolve(string? evaluatorKeyCount)
    {
        var settings = new Dictionary<string, string?>();
        if (evaluatorKeyCount is not null)
            settings["Fleans:Worker:EvaluatorKeyCount"] = evaluatorKeyCount;

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddApplication();

        return services.BuildServiceProvider().GetRequiredService<IOptions<WorkerEvaluatorOptions>>().Value;
    }

    [TestMethod]
    public void Default_SpreadsCallsOverSixteenKeys()
    {
        var options = Resolve(null);

        var keys = Enumerable.Range(0, 2_000).Select(_ => options.NextKey()).ToHashSet();

        Assert.AreEqual(16, options.EvaluatorKeyCount);
        Assert.IsTrue(keys.All(k => k is >= 0 and < 16), "Key out of range.");
        Assert.IsTrue(keys.Count > 1,
            "A single fixed key pins every call from a core silo to one worker silo (#857).");
    }

    [TestMethod]
    public void ConfiguredKeyCount_BoundsTheKeys()
    {
        var options = Resolve("3");

        var keys = Enumerable.Range(0, 2_000).Select(_ => options.NextKey()).ToHashSet();

        Assert.AreEqual(3, options.EvaluatorKeyCount);
        CollectionAssert.AreEquivalent(new long[] { 0, 1, 2 }, keys.ToArray());
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    public void NonPositiveKeyCount_IsRejected(string value)
    {
        var ex = Assert.ThrowsExactly<OptionsValidationException>(() => Resolve(value));

        StringAssert.Contains(ex.Message, "Fleans:Worker:EvaluatorKeyCount must be greater than 0.");
    }
}
