namespace Fleans.Domain.States;

/// <summary>
/// A request to deploy one BPMN process that needs <see cref="RequiredApprovals"/> approvals
/// from distinct reviewers first (#900). Persisted by <c>DeploymentRequestGrain</c> in the
/// <c>DeploymentRequests</c> table and mutated only through the methods below, each of which
/// applies one whole transition:
/// <code>
/// Submit ─► Pending ─approve×N─► Approved ─► Deployed
///             │  └─ reject ─► Rejected       │ ▲ (deploy failed: DeployError kept, retry)
///             └─ withdraw ─► Withdrawn ◄─────┘─┘
/// </code>
/// Who may review (approve or reject): an identified user who is not the requester, counted once,
/// and — when <see cref="ReviewerGroups"/> is non-empty — a member of one of those groups.
/// </summary>
[GenerateSerializer]
public class DeploymentRequestState
{
    [Id(0)] public Guid Id { get; set; }
    [Id(1)] public string ProcessDefinitionKey { get; set; } = "";
    [Id(2)] public string BpmnXml { get; set; } = "";
    [Id(3)] public string RequestedBy { get; set; } = "";
    [Id(4)] public DateTimeOffset RequestedAt { get; set; }
    [Id(5)] public string? Comment { get; set; }
    [Id(6)] public int RequiredApprovals { get; set; }
    [Id(7)] public bool DeployDisabled { get; set; }
    [Id(8)] public DeploymentRequestStatus Status { get; set; }
    [Id(9)] public List<DeploymentApproval> Approvals { get; set; } = new();
    /// <summary>Who rejected or withdrew the request.</summary>
    [Id(10)] public string? ClosedBy { get; set; }
    [Id(11)] public DateTimeOffset? ClosedAt { get; set; }
    /// <summary>The rejection reason.</summary>
    [Id(12)] public string? CloseReason { get; set; }
    [Id(13)] public string? DeployedProcessDefinitionId { get; set; }
    [Id(14)] public int? DeployedVersion { get; set; }
    [Id(15)] public DateTimeOffset? DeployedAt { get; set; }
    /// <summary>Why the last deploy attempt of an approved request failed; cleared on success.</summary>
    [Id(16)] public string? DeployError { get; set; }
    [Id(17)] public string ETag { get; set; } = "";
    /// <summary>Groups a reviewer must belong to (any one of them); empty: anyone but the requester.</summary>
    [Id(18)] public List<string> ReviewerGroups { get; set; } = new();

    public bool IsOpen => Status is DeploymentRequestStatus.Pending or DeploymentRequestStatus.Approved;

    public void Submit(Guid id, string processDefinitionKey, string bpmnXml, string requestedBy,
        string? comment, int requiredApprovals, bool deployDisabled, DateTimeOffset at,
        IReadOnlyCollection<string>? reviewerGroups = null)
    {
        if (Id != Guid.Empty)
            throw new InvalidOperationException($"Deployment request {Id} was already submitted.");
        if (string.IsNullOrWhiteSpace(processDefinitionKey))
            throw new ArgumentException("The BPMN process has no id.", nameof(processDefinitionKey));
        if (string.IsNullOrWhiteSpace(bpmnXml))
            throw new ArgumentException("BpmnXml is required.", nameof(bpmnXml));
        RequireUser(requestedBy);
        if (requiredApprovals < 0)
            throw new ArgumentOutOfRangeException(nameof(requiredApprovals), "Required approvals can't be negative.");

        Id = id;
        ProcessDefinitionKey = processDefinitionKey;
        BpmnXml = bpmnXml;
        RequestedBy = requestedBy;
        RequestedAt = at;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment;
        RequiredApprovals = requiredApprovals;
        DeployDisabled = deployDisabled;
        ReviewerGroups = reviewerGroups?.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct().ToList() ?? new();
        Status = requiredApprovals == 0 ? DeploymentRequestStatus.Approved : DeploymentRequestStatus.Pending;
    }

    /// <summary>Records <paramref name="userId"/>'s approval; the last one needed moves the request to Approved.</summary>
    public void Approve(string userId, IReadOnlyCollection<string> userGroups, string? comment, DateTimeOffset at)
    {
        RequireStatus(DeploymentRequestStatus.Pending, "approved");
        RequireUser(userId);
        if (string.Equals(userId, RequestedBy, StringComparison.Ordinal))
            throw new InvalidOperationException("The requester can't approve their own deployment request.");
        RequireReviewerGroup(userGroups);
        if (Approvals.Any(a => string.Equals(a.UserId, userId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"'{userId}' has already approved this deployment request.");

        Approvals.Add(new DeploymentApproval(userId, at, string.IsNullOrWhiteSpace(comment) ? null : comment));
        if (Approvals.Count >= RequiredApprovals)
            Status = DeploymentRequestStatus.Approved;
    }

    public void Reject(string userId, IReadOnlyCollection<string> userGroups, string reason, DateTimeOffset at)
    {
        RequireStatus(DeploymentRequestStatus.Pending, "rejected");
        RequireUser(userId);
        if (string.Equals(userId, RequestedBy, StringComparison.Ordinal))
            throw new InvalidOperationException("The requester can't reject their own deployment request — withdraw it instead.");
        RequireReviewerGroup(userGroups);
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection needs a reason.", nameof(reason));

        Status = DeploymentRequestStatus.Rejected;
        ClosedBy = userId;
        ClosedAt = at;
        CloseReason = reason;
    }

    public void Withdraw(string userId, DateTimeOffset at)
    {
        RequireSubmitted();
        if (!IsOpen)
            throw new InvalidOperationException($"A {Status} deployment request can't be withdrawn.");
        if (!string.Equals(userId, RequestedBy, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the requester can withdraw a deployment request.");

        Status = DeploymentRequestStatus.Withdrawn;
        ClosedBy = userId;
        ClosedAt = at;
    }

    public void MarkDeployed(string processDefinitionId, int version, DateTimeOffset at)
    {
        RequireStatus(DeploymentRequestStatus.Approved, "deployed");

        Status = DeploymentRequestStatus.Deployed;
        DeployedProcessDefinitionId = processDefinitionId;
        DeployedVersion = version;
        DeployedAt = at;
        DeployError = null;
    }

    /// <summary>True when <paramref name="userGroups"/> satisfies <see cref="ReviewerGroups"/>.</summary>
    public bool IsReviewerGroupMember(IReadOnlyCollection<string> userGroups) =>
        ReviewerGroups.Count == 0 || ReviewerGroups.Any(g => userGroups.Contains(g, StringComparer.Ordinal));

    /// <summary>Keeps the request Approved so the deploy can be retried.</summary>
    public void MarkDeployFailed(string error)
    {
        RequireStatus(DeploymentRequestStatus.Approved, "deployed");
        DeployError = error;
    }

    private void RequireSubmitted()
    {
        if (Id == Guid.Empty)
            throw new KeyNotFoundException("Deployment request not found.");
    }

    private void RequireStatus(DeploymentRequestStatus expected, string action)
    {
        RequireSubmitted();
        if (Status != expected)
            throw new InvalidOperationException($"A {Status} deployment request can't be {action}.");
    }

    private void RequireReviewerGroup(IReadOnlyCollection<string> userGroups)
    {
        if (!IsReviewerGroupMember(userGroups))
            throw new InvalidOperationException(
                $"Only members of {string.Join(" or ", ReviewerGroups)} can review this deployment request.");
    }

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A user id is required.", nameof(userId));
    }
}

[GenerateSerializer]
public enum DeploymentRequestStatus
{
    Pending,
    Approved,
    Deployed,
    Rejected,
    Withdrawn,
}

[GenerateSerializer]
public record DeploymentApproval(
    [property: Id(0)] string UserId,
    [property: Id(1)] DateTimeOffset ApprovedAt,
    [property: Id(2)] string? Comment);
