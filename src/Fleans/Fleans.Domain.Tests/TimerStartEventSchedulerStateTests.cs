using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerStartEventSchedulerStateTests
{
    [TestMethod]
    public void Activate_AfterPreviousScheduleFired_ResetsFireCount()
    {
        // Arrange — a previous R2 schedule fired twice
        var state = new TimerStartEventSchedulerState();
        state.Activate("proc:1:a", maxFireCount: 2);
        state.IncrementFireCount();
        state.IncrementFireCount();

        // Act — redeploy / re-enable arms a new schedule
        state.Activate("proc:2:b", maxFireCount: 2);

        // Assert — the new schedule gets its full repetition budget
        Assert.AreEqual(0, state.FireCount);
        Assert.AreEqual(2, state.MaxFireCount);
        Assert.AreEqual("proc:2:b", state.ProcessDefinitionId);
    }
}
