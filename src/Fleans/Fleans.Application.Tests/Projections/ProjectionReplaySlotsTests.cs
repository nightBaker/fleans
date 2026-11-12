using Fleans.Application.Projections;
using Microsoft.Extensions.Options;

namespace Fleans.Application.Tests.Projections;

[TestClass]
public class ProjectionReplaySlotsTests
{
    [TestMethod]
    public async Task EnterAsync_PastTheLimit_WaitsUntilASlotIsFreed()
    {
        // Arrange
        var slots = new ProjectionReplaySlots(Options.Create(new ProjectorOptions { MaxConcurrentReplays = 2 }));
        var first = await slots.EnterAsync();
        var second = await slots.EnterAsync();

        // Act
        var third = slots.EnterAsync().AsTask();
        await Task.Delay(100);

        // Assert
        Assert.IsFalse(third.IsCompleted, "a third read waits while both slots are taken");
        first.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        second.Dispose();
    }
}
