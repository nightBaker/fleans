using Fleans.Application.Editor;

namespace Fleans.Application.Tests.Editor;

[TestClass]
public class EditorPresenceGrainTests : WorkflowTestBase
{
    private static EditorPresence Session(string id, string name, string? element = null) =>
        new(id, name, element, element, DateTimeOffset.MinValue);

    [TestMethod]
    public async Task Heartbeat_ReturnsOtherSessionsOnly_WithTheirSelection()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IEditorPresenceGrain>("presence-" + Guid.NewGuid().ToString("N"));
        await grain.Heartbeat(Session("a", "Ann"));

        // Act
        var seenByBob = await grain.Heartbeat(Session("b", "Bob", "Task_Review"));
        var seenByAnn = await grain.Heartbeat(Session("a", "Ann"));

        // Assert
        Assert.AreEqual(1, seenByBob.Count);
        Assert.AreEqual("Ann", seenByBob[0].DisplayName);
        Assert.AreEqual(1, seenByAnn.Count);
        Assert.AreEqual("Task_Review", seenByAnn[0].SelectedElementId);
        Assert.AreNotEqual(DateTimeOffset.MinValue, seenByAnn[0].SeenAt, "SeenAt is stamped by the grain.");
    }

    [TestMethod]
    public async Task Leave_RemovesTheSession_AndUnknownIdsAreIgnored()
    {
        // Arrange
        var grain = Cluster.GrainFactory.GetGrain<IEditorPresenceGrain>("presence-" + Guid.NewGuid().ToString("N"));
        await grain.Heartbeat(Session("a", "Ann"));
        await grain.Heartbeat(Session("b", "Bob"));

        // Act
        await grain.Leave("b");
        await grain.Leave("does-not-exist");
        var seenByAnn = await grain.Heartbeat(Session("a", "Ann"));

        // Assert
        Assert.AreEqual(0, seenByAnn.Count);
    }

    [TestMethod]
    public async Task Sessions_AreScopedToTheProcessKey()
    {
        // Arrange
        var key = Guid.NewGuid().ToString("N");
        await Cluster.GrainFactory.GetGrain<IEditorPresenceGrain>("one-" + key).Heartbeat(Session("a", "Ann"));

        // Act
        var other = await Cluster.GrainFactory.GetGrain<IEditorPresenceGrain>("two-" + key).Heartbeat(Session("b", "Bob"));

        // Assert
        Assert.AreEqual(0, other.Count);
    }
}
