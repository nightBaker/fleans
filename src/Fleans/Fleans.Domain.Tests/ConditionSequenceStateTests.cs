using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class ConditionSequenceStateTests
{
    [TestMethod]
    public void IsEvaluated_ShouldBeFalse_WhenCreated()
    {
        var source = new ExclusiveGateway("gw1");
        var target = new EndEvent("end1");
        var flow = new ConditionalSequenceFlow("seq1", source, target, "x > 5");
        var state = new ConditionSequenceState(flow);
        Assert.IsFalse(state.IsEvaluated);
        Assert.IsFalse(state.Result);
    }

    [TestMethod]
    public void IsEvaluated_ShouldBeTrue_AfterSetResultTrue()
    {
        var source = new ExclusiveGateway("gw1");
        var target = new EndEvent("end1");
        var flow = new ConditionalSequenceFlow("seq1", source, target, "x > 5");
        var state = new ConditionSequenceState(flow);
        state.SetResult(true);
        Assert.IsTrue(state.IsEvaluated);
        Assert.IsTrue(state.Result);
    }

    [TestMethod]
    public void IsEvaluated_ShouldBeTrue_AfterSetResultFalse()
    {
        var source = new ExclusiveGateway("gw1");
        var target = new EndEvent("end1");
        var flow = new ConditionalSequenceFlow("seq1", source, target, "x > 5");
        var state = new ConditionSequenceState(flow);
        state.SetResult(false);
        Assert.IsTrue(state.IsEvaluated);
        Assert.IsFalse(state.Result);
    }
}
