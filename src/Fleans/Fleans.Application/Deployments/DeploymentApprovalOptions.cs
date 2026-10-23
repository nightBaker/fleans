namespace Fleans.Application.Deployments;

/// <summary>
/// The deploy-approval policy of this Fleans cluster, bound from <c>Fleans:Deployments</c> (#900).
/// Read by <see cref="WorkflowCommandService"/> in every host (Api, Web, Mcp), so set it on all of
/// them — the same way <c>Authentication:*</c> is configured per host.
/// </summary>
public sealed class DeploymentApprovalOptions
{
    public const string SectionName = "Fleans:Deployments";

    /// <summary>
    /// How many distinct reviewers (never the requester) must approve a deployment request before
    /// the version is deployed. <c>0</c> (default) deploys immediately; above it, direct deploys are
    /// refused with <see cref="DeploymentApprovalRequiredException"/> and go through
    /// <see cref="IWorkflowCommandService.SubmitDeployment"/> instead.
    /// </summary>
    public int RequiredApprovals { get; set; }
}
