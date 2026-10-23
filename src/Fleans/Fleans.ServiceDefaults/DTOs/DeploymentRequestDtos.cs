namespace Fleans.ServiceDefaults.DTOs;

// Deployment requests (#900) — POST /Deployments/requests and the review actions.

/// <param name="RequestedBy">
/// The requester. Required when auth is disabled. Under JWT auth the user comes from the token;
/// this field is optional and, if sent, must match the token.
/// </param>
/// <param name="DeployDisabled">Deploy the version disabled: no new instances until someone enables it.</param>
public record SubmitDeploymentRequest(string BpmnXml, string? Comment = null, bool DeployDisabled = false, string? RequestedBy = null);

/// <param name="UserId">The reviewer; same rules as <see cref="SubmitDeploymentRequest.RequestedBy"/>.</param>
/// <param name="UserGroups">The reviewer's groups when auth is disabled; ignored under JWT auth, where
/// they come from the token's <c>Authentication:GroupsClaim</c>. Checked against the request's
/// <c>ReviewerGroups</c>.</param>
public record ApproveDeploymentRequest(string? UserId = null, string? Comment = null, IReadOnlyList<string>? UserGroups = null);

/// <param name="UserId">The reviewer; same rules as <see cref="SubmitDeploymentRequest.RequestedBy"/>.</param>
/// <param name="UserGroups">Same rules as <see cref="ApproveDeploymentRequest.UserGroups"/>.</param>
public record RejectDeploymentRequest(string Reason, string? UserId = null, IReadOnlyList<string>? UserGroups = null);

/// <param name="UserId">The requester; same rules as <see cref="SubmitDeploymentRequest.RequestedBy"/>.</param>
public record WithdrawDeploymentRequest(string? UserId = null);

/// <param name="Status">Pending, Approved, Deployed, Rejected or Withdrawn.</param>
/// <param name="DeployError">Set while an approved request's deploy has failed; retry with POST …/retry.</param>
/// <param name="ReviewerGroups">Groups a reviewer must belong to (any one); empty: anyone but the requester.</param>
public record DeploymentRequestDto(
    Guid Id,
    string ProcessDefinitionKey,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? Comment,
    int RequiredApprovals,
    List<DeploymentApprovalDto> Approvals,
    string Status,
    bool DeployDisabled,
    string? ClosedBy,
    string? CloseReason,
    int? DeployedVersion,
    string? DeployError,
    List<string> ReviewerGroups);

public record DeploymentApprovalDto(string UserId, DateTimeOffset ApprovedAt, string? Comment);
