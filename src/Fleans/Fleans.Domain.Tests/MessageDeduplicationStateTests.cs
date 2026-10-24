using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class MessageDeduplicationStateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void NewState_IsNotRecorded()
    {
        Assert.IsFalse(new MessageDeduplicationState().IsRecorded(At));
    }

    [TestMethod]
    public void StartedRecord_CountsUntilTheWindowEnds()
    {
        // Arrange
        var state = new MessageDeduplicationState();
        var instanceId = Guid.NewGuid();

        // Act
        state.PlanStarts([new PlannedStart("order-process", instanceId)], At);
        state.CompleteStarts([instanceId]);

        // Assert
        Assert.AreEqual(MessageDeduplicationStatus.Started, state.Status);
        CollectionAssert.AreEqual(new[] { instanceId }, state.StartedInstanceIds);
        Assert.IsTrue(state.IsRecorded(At + MessageDeduplicationState.Window - TimeSpan.FromSeconds(1)));
        Assert.IsFalse(state.IsRecorded(At + MessageDeduplicationState.Window));
    }

    [TestMethod]
    public void PlannedRecord_IsRecordedAsStarting()
    {
        var state = new MessageDeduplicationState();

        state.PlanStarts([new PlannedStart("order-process", Guid.NewGuid())], At);

        Assert.AreEqual(MessageDeduplicationStatus.Starting, state.Status);
        Assert.IsTrue(state.IsRecorded(At));
    }

    [TestMethod]
    public void CompleteStarts_WithNoInstance_DropsTheRecord()
    {
        // Arrange
        var state = new MessageDeduplicationState();
        state.PlanStarts([new PlannedStart("order-process", Guid.NewGuid())], At);

        // Act
        state.CompleteStarts([]);

        // Assert
        Assert.AreEqual(MessageDeduplicationStatus.None, state.Status);
        Assert.IsFalse(state.IsRecorded(At));
        Assert.IsEmpty(state.PlannedStarts);
    }

    [TestMethod]
    public void RecordCorrelated_AfterAnExpiredStart_ReplacesIt()
    {
        // Arrange
        var state = new MessageDeduplicationState();
        var instanceId = Guid.NewGuid();
        state.PlanStarts([new PlannedStart("order-process", instanceId)], At);
        state.CompleteStarts([instanceId]);
        var later = At + MessageDeduplicationState.Window;

        // Act
        state.RecordCorrelated(later);

        // Assert
        Assert.AreEqual(MessageDeduplicationStatus.Correlated, state.Status);
        Assert.AreEqual(later, state.ReceivedAt);
        Assert.IsEmpty(state.StartedInstanceIds);
    }

    [TestMethod]
    public void PlanStarts_WithNoProcess_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new MessageDeduplicationState().PlanStarts([], At));
    }

    [TestMethod]
    public void CompleteStarts_WithoutAPlan_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new MessageDeduplicationState().CompleteStarts([Guid.NewGuid()]));
    }
}
