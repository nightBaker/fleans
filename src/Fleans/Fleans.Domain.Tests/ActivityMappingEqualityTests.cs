using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class ActivityMappingEqualityTests
{
    [TestMethod]
    public void ActivitiesWithEqualMappings_AreEqual()
    {
        // A definition read back from storage holds new lists; sequence flows still find their activities.
        var a = new ScriptTask("s", "x") { InputMappings = [new InputMapping("=a", "b")], OutputMappings = [new OutputMapping("=c", "d")] };
        var b = new ScriptTask("s", "x") { InputMappings = [new InputMapping("=a", "b")], OutputMappings = [new OutputMapping("=c", "d")] };

        Assert.AreEqual(a, b);
        Assert.AreEqual(a.GetHashCode(), b.GetHashCode());
        Assert.AreEqual(new TaskActivity("t"), new TaskActivity("t"));
    }

    [TestMethod]
    public void ActivitiesWithDifferentMappings_AreNotEqual()
    {
        var plain = new ScriptTask("s", "x");
        var mapped = plain with { InputMappings = [new InputMapping("=a", "b")] };

        Assert.AreNotEqual(plain, mapped);
        Assert.AreNotEqual(mapped, mapped with { InputMappings = [new InputMapping("=a", "other")] });
        Assert.AreNotEqual<Activity>(new TaskActivity("t"), new ScriptTask("t", "x"));
    }
}
