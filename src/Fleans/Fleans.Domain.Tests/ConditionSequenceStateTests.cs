using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class ConditionSequenceStateTests
{
    [TestMethod]
    public void IsEvaluated_ShouldBeFalse_WhenCreated()
    {
        var state = new ConditionSequenceState("seq1");
        Assert.IsFalse(state.IsEvaluated);
        Assert.IsFalse(state.Result);
    }

    [TestMethod]
    public void IsEvaluated_ShouldBeTrue_AfterSetResultTrue()
    {
        var state = new ConditionSequenceState("seq1");
        state.SetResult(true);
        Assert.IsTrue(state.IsEvaluated);
        Assert.IsTrue(state.Result);
    }

    [TestMethod]
    public void IsEvaluated_ShouldBeTrue_AfterSetResultFalse()
    {
        var state = new ConditionSequenceState("seq1");
        state.SetResult(false);
        Assert.IsTrue(state.IsEvaluated);
        Assert.IsFalse(state.Result);
    }
}
