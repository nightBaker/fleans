using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class DeploymentRequestStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static DeploymentRequestState Submitted(int requiredApprovals = 2, string requestedBy = "alice")
    {
        var request = new DeploymentRequestState();
        request.Submit(Guid.NewGuid(), "order-process", "<bpmn/>", requestedBy, "adds a timer",
            requiredApprovals, deployDisabled: false, Now);
        return request;
    }

    [TestMethod]
    public void Submit_WithApprovalsRequired_IsPending()
    {
        var request = Submitted(requiredApprovals: 2);

        Assert.AreEqual(DeploymentRequestStatus.Pending, request.Status);
        Assert.AreEqual("order-process", request.ProcessDefinitionKey);
        Assert.AreEqual("alice", request.RequestedBy);
        Assert.AreEqual(2, request.RequiredApprovals);
        Assert.AreEqual(Now, request.RequestedAt);
    }

    [TestMethod]
    public void Submit_WithNoApprovalsRequired_IsApprovedAtOnce()
    {
        var request = Submitted(requiredApprovals: 0);

        Assert.AreEqual(DeploymentRequestStatus.Approved, request.Status);
    }

    [TestMethod]
    public void Submit_Twice_Throws()
    {
        var request = Submitted();

        Assert.Throws<InvalidOperationException>(() =>
            request.Submit(Guid.NewGuid(), "x", "<bpmn/>", "bob", null, 1, false, Now));
    }

    [TestMethod]
    public void Submit_WithoutRequester_Throws()
    {
        var request = new DeploymentRequestState();

        Assert.Throws<ArgumentException>(() =>
            request.Submit(Guid.NewGuid(), "x", "<bpmn/>", " ", null, 1, false, Now));
    }

    [TestMethod]
    public void Approve_BelowTheRequiredCount_StaysPending()
    {
        var request = Submitted(requiredApprovals: 2);

        request.Approve("bob", [], "looks good", Now);

        Assert.AreEqual(DeploymentRequestStatus.Pending, request.Status);
        Assert.AreEqual(1, request.Approvals.Count);
        Assert.AreEqual("bob", request.Approvals[0].UserId);
        Assert.AreEqual("looks good", request.Approvals[0].Comment);
    }

    [TestMethod]
    public void Approve_ReachingTheRequiredCount_IsApproved()
    {
        var request = Submitted(requiredApprovals: 2);

        request.Approve("bob", [], null, Now);
        request.Approve("carol", [], null, Now);

        Assert.AreEqual(DeploymentRequestStatus.Approved, request.Status);
    }

    [TestMethod]
    public void Approve_ByTheRequester_Throws()
    {
        var request = Submitted(requestedBy: "alice");

        var ex = Assert.Throws<InvalidOperationException>(() => request.Approve("alice", [], null, Now));

        StringAssert.Contains(ex.Message, "requester");
        Assert.AreEqual(0, request.Approvals.Count);
    }

    [TestMethod]
    public void Approve_TwiceBySameUser_CountsOnce()
    {
        var request = Submitted(requiredApprovals: 2);
        request.Approve("bob", [], null, Now);

        Assert.Throws<InvalidOperationException>(() => request.Approve("bob", [], null, Now));

        Assert.AreEqual(1, request.Approvals.Count);
        Assert.AreEqual(DeploymentRequestStatus.Pending, request.Status);
    }

    [TestMethod]
    public void Approve_WithoutUser_Throws()
    {
        var request = Submitted();

        Assert.Throws<ArgumentException>(() => request.Approve("", [], null, Now));
    }

    [TestMethod]
    public void Approve_ARejectedRequest_Throws()
    {
        var request = Submitted();
        request.Reject("bob", [], "missing error handling", Now);

        Assert.Throws<InvalidOperationException>(() => request.Approve("carol", [], null, Now));
    }

    [TestMethod]
    public void Approve_BeforeSubmit_ThrowsNotFound()
    {
        Assert.Throws<KeyNotFoundException>(() => new DeploymentRequestState().Approve("bob", [], null, Now));
    }

    [TestMethod]
    public void Reject_ByAReviewer_ClosesTheRequestWithTheReason()
    {
        var request = Submitted();

        request.Reject("bob", [], "missing error handling", Now);

        Assert.AreEqual(DeploymentRequestStatus.Rejected, request.Status);
        Assert.AreEqual("bob", request.ClosedBy);
        Assert.AreEqual("missing error handling", request.CloseReason);
        Assert.AreEqual(Now, request.ClosedAt);
    }

    [TestMethod]
    public void Reject_WithoutReason_Throws()
    {
        var request = Submitted();

        Assert.Throws<ArgumentException>(() => request.Reject("bob", [], " ", Now));
        Assert.AreEqual(DeploymentRequestStatus.Pending, request.Status);
    }

    [TestMethod]
    public void Reject_ByTheRequester_Throws()
    {
        var request = Submitted(requestedBy: "alice");

        Assert.Throws<InvalidOperationException>(() => request.Reject("alice", [], "never mind", Now));
    }

    [TestMethod]
    public void Withdraw_ByTheRequester_ClosesTheRequest()
    {
        var request = Submitted(requestedBy: "alice");

        request.Withdraw("alice", Now);

        Assert.AreEqual(DeploymentRequestStatus.Withdrawn, request.Status);
        Assert.AreEqual("alice", request.ClosedBy);
    }

    [TestMethod]
    public void Withdraw_BySomeoneElse_Throws()
    {
        var request = Submitted(requestedBy: "alice");

        Assert.Throws<InvalidOperationException>(() => request.Withdraw("bob", Now));
    }

    [TestMethod]
    public void Withdraw_ADeployedRequest_Throws()
    {
        var request = Submitted(requiredApprovals: 0);
        request.MarkDeployed("order-process:1:x", 1, Now);

        Assert.Throws<InvalidOperationException>(() => request.Withdraw("alice", Now));
    }

    [TestMethod]
    public void MarkDeployed_AnApprovedRequest_RecordsTheVersion()
    {
        var request = Submitted(requiredApprovals: 0);
        request.MarkDeployFailed("storage down");

        request.MarkDeployed("order-process:3:x", 3, Now);

        Assert.AreEqual(DeploymentRequestStatus.Deployed, request.Status);
        Assert.AreEqual(3, request.DeployedVersion);
        Assert.AreEqual("order-process:3:x", request.DeployedProcessDefinitionId);
        Assert.IsNull(request.DeployError);
    }

    [TestMethod]
    public void MarkDeployed_APendingRequest_Throws()
    {
        var request = Submitted(requiredApprovals: 1);

        Assert.Throws<InvalidOperationException>(() => request.MarkDeployed("p:1:x", 1, Now));
    }

    [TestMethod]
    public void MarkDeployFailed_KeepsTheRequestApproved()
    {
        var request = Submitted(requiredApprovals: 0);

        request.MarkDeployFailed("storage down");

        Assert.AreEqual(DeploymentRequestStatus.Approved, request.Status);
        Assert.AreEqual("storage down", request.DeployError);
        Assert.IsTrue(request.IsOpen);
    }

    private static DeploymentRequestState SubmittedForGroups(params string[] reviewerGroups)
    {
        var request = new DeploymentRequestState();
        request.Submit(Guid.NewGuid(), "order-process", "<bpmn/>", "alice", null, 1, false, Now, reviewerGroups);
        return request;
    }

    [TestMethod]
    public void Submit_KeepsDistinctNonBlankReviewerGroups()
    {
        var request = SubmittedForGroups("release-managers", " ", "release-managers", "sre");

        CollectionAssert.AreEqual(new[] { "release-managers", "sre" }, request.ReviewerGroups);
    }

    [TestMethod]
    public void Approve_ByANonMember_WhenReviewerGroupsAreSet_Throws()
    {
        var request = SubmittedForGroups("release-managers");

        var ex = Assert.Throws<InvalidOperationException>(() => request.Approve("bob", ["developers"], null, Now));

        StringAssert.Contains(ex.Message, "release-managers");
        Assert.IsEmpty(request.Approvals);
    }

    [TestMethod]
    public void Approve_ByAMemberOfAnyReviewerGroup_Counts()
    {
        var request = SubmittedForGroups("release-managers", "sre");

        request.Approve("bob", ["developers", "sre"], null, Now);

        Assert.AreEqual(DeploymentRequestStatus.Approved, request.Status);
    }

    [TestMethod]
    public void Reject_ByANonMember_WhenReviewerGroupsAreSet_Throws()
    {
        var request = SubmittedForGroups("release-managers");

        Assert.Throws<InvalidOperationException>(() => request.Reject("bob", [], "no", Now));
        Assert.AreEqual(DeploymentRequestStatus.Pending, request.Status);
    }

    [TestMethod]
    public void Approve_WithoutReviewerGroups_IgnoresTheReviewersGroups()
    {
        var request = Submitted(requiredApprovals: 1);

        request.Approve("bob", [], null, Now);

        Assert.AreEqual(DeploymentRequestStatus.Approved, request.Status);
    }
}
