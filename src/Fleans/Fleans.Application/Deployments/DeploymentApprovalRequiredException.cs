namespace Fleans.Application.Deployments;

/// <summary>
/// A direct deploy refused because this cluster requires deploy approvals (#900). An
/// <see cref="InvalidOperationException"/>, so the API answers 409.
/// </summary>
public sealed class DeploymentApprovalRequiredException(int requiredApprovals)
    : InvalidOperationException(
        $"This cluster requires {requiredApprovals} approval(s) per deployment. " +
        "Submit a deployment request instead (POST /Deployments/requests, or Deploy in the Admin UI).");
