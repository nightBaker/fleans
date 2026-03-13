using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class UserTaskMetadataTests
{
    private static UserTaskMetadata CreateInitialized(string? assignee = "john")
    {
        var metadata = new UserTaskMetadata();
        metadata.Initialize(assignee, ["group1"], ["user1"], ["var1"]);
        return metadata;
    }

    // --- Initialize ---

    [TestMethod]
    public void Initialize_ShouldSetAllProperties_AndStateCreated()
    {
        // Arrange
        var metadata = new UserTaskMetadata();

        // Act
        metadata.Initialize("alice", ["admins"], ["bob", "carol"], ["outputA"]);

        // Assert
        Assert.AreEqual("alice", metadata.Assignee);
        CollectionAssert.AreEqual(new[] { "admins" }, metadata.CandidateGroups);
        CollectionAssert.AreEqual(new[] { "bob", "carol" }, metadata.CandidateUsers);
        CollectionAssert.AreEqual(new[] { "outputA" }, metadata.ExpectedOutputVariables);
        Assert.AreEqual(UserTaskLifecycleState.Created, metadata.TaskState);
        Assert.IsNull(metadata.ClaimedBy);
        Assert.IsNull(metadata.ClaimedAt);
    }

    // --- Claim ---

    [TestMethod]
    public void Claim_FromCreatedState_ShouldSucceed()
    {
        // Arrange
        var metadata = CreateInitialized();
        var claimedAt = DateTimeOffset.UtcNow;

        // Act
        metadata.Claim("john", claimedAt);

        // Assert
        Assert.AreEqual(UserTaskLifecycleState.Claimed, metadata.TaskState);
        Assert.AreEqual("john", metadata.ClaimedBy);
        Assert.AreEqual(claimedAt, metadata.ClaimedAt);
    }

    [TestMethod]
    public void Claim_FromNonCreatedState_ShouldThrowInvalidOperation()
    {
        // Arrange
        var metadata = CreateInitialized();
        metadata.Claim("john", DateTimeOffset.UtcNow);

        // Act & Assert - already Claimed
        Assert.ThrowsExactly<InvalidOperationException>(
            () => metadata.Claim("john", DateTimeOffset.UtcNow));
    }

    // --- Unclaim ---

    [TestMethod]
    public void Unclaim_FromClaimedState_ShouldSucceed_AndResetToCreated()
    {
        // Arrange
        var metadata = CreateInitialized();
        metadata.Claim("john", DateTimeOffset.UtcNow);

        // Act
        metadata.Unclaim();

        // Assert
        Assert.AreEqual(UserTaskLifecycleState.Created, metadata.TaskState);
        Assert.IsNull(metadata.ClaimedBy);
        Assert.IsNull(metadata.ClaimedAt);
    }

    [TestMethod]
    public void Unclaim_FromNonClaimedState_ShouldThrowInvalidOperation()
    {
        // Arrange
        var metadata = CreateInitialized(); // Created state, not claimed

        // Act & Assert
        Assert.ThrowsExactly<InvalidOperationException>(() => metadata.Unclaim());
    }

    // --- Complete ---

    [TestMethod]
    public void Complete_FromClaimedState_ShouldSucceed()
    {
        // Arrange
        var metadata = CreateInitialized();
        metadata.Claim("john", DateTimeOffset.UtcNow);

        // Act
        metadata.Complete();

        // Assert
        Assert.AreEqual(UserTaskLifecycleState.Completed, metadata.TaskState);
    }

    [TestMethod]
    public void Complete_FromNonClaimedState_ShouldThrowInvalidOperation()
    {
        // Arrange
        var metadata = CreateInitialized(); // Created state, not claimed

        // Act & Assert
        Assert.ThrowsExactly<InvalidOperationException>(() => metadata.Complete());
    }
}
