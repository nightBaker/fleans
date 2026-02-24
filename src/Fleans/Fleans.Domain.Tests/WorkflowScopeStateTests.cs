using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class WorkflowScopeStateTests
{
    [TestMethod]
    public void TrackChild_ShouldAddToActiveSet()
    {
        var scope = new WorkflowScopeState(Guid.NewGuid(), null, Guid.NewGuid(), "sp1", Guid.NewGuid());
        var childId = Guid.NewGuid();

        scope.TrackChild(childId);

        Assert.IsFalse(scope.IsComplete);
        Assert.IsTrue(scope.ActiveChildInstanceIds.Contains(childId));
    }

    [TestMethod]
    public void UntrackChild_ShouldRemoveFromSet_AndReturnTrueWhenEmpty()
    {
        var scope = new WorkflowScopeState(Guid.NewGuid(), null, Guid.NewGuid(), "sp1", Guid.NewGuid());
        var childId = Guid.NewGuid();
        scope.TrackChild(childId);

        var isComplete = scope.UntrackChild(childId);

        Assert.IsTrue(isComplete);
        Assert.IsTrue(scope.IsComplete);
    }

    [TestMethod]
    public void UntrackChild_ShouldReturnFalse_WhenChildrenRemain()
    {
        var scope = new WorkflowScopeState(Guid.NewGuid(), null, Guid.NewGuid(), "sp1", Guid.NewGuid());
        var child1 = Guid.NewGuid();
        var child2 = Guid.NewGuid();
        scope.TrackChild(child1);
        scope.TrackChild(child2);

        var isComplete = scope.UntrackChild(child1);

        Assert.IsFalse(isComplete);
        Assert.IsFalse(scope.IsComplete);
    }

    [TestMethod]
    public void DrainActiveChildren_ShouldReturnAllChildren_AndClearSet()
    {
        var scope = new WorkflowScopeState(Guid.NewGuid(), null, Guid.NewGuid(), "sp1", Guid.NewGuid());
        var child1 = Guid.NewGuid();
        var child2 = Guid.NewGuid();
        scope.TrackChild(child1);
        scope.TrackChild(child2);

        var drained = scope.DrainActiveChildren();

        Assert.HasCount(2, drained);
        Assert.IsTrue(drained.Contains(child1));
        Assert.IsTrue(drained.Contains(child2));
        Assert.IsTrue(scope.IsComplete);
    }

    [TestMethod]
    public void IsComplete_ShouldBeTrue_WhenNoChildrenTracked()
    {
        var scope = new WorkflowScopeState(Guid.NewGuid(), null, Guid.NewGuid(), "sp1", Guid.NewGuid());
        Assert.IsTrue(scope.IsComplete);
    }
}
