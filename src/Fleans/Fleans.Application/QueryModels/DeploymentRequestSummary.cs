using Fleans.Domain.States;

namespace Fleans.Application.QueryModels;

/// <summary>A row of the deployment review queue (#900): the request without its BPMN XML.</summary>
public sealed record DeploymentRequestSummary(
    Guid Id,
    string ProcessDefinitionKey,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    string? Comment,
    int RequiredApprovals,
    List<DeploymentApproval> Approvals,
    DeploymentRequestStatus Status,
    bool DeployDisabled,
    string? ClosedBy,
    string? CloseReason,
    int? DeployedVersion,
    string? DeployError,
    List<string> ReviewerGroups);
