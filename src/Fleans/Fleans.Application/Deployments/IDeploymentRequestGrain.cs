using Fleans.Domain.States;
using Orleans.Concurrency;

namespace Fleans.Application.Deployments;

/// <summary>
/// One deployment request (#900), keyed by its id. Holds the BPMN XML and the approvals, and
/// deploys the XML itself once the last required approval arrives — so a request needs no caller
/// to finish it. Transition rules live on <see cref="DeploymentRequestState"/>. Every method
/// returns the state after the call.
/// </summary>
public interface IDeploymentRequestGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Validates and stores the request. With <paramref name="requiredApprovals"/> = 0 it deploys
    /// at once. Throws <see cref="ArgumentException"/> for invalid BPMN.
    /// </summary>
    /// <param name="reviewerGroups">Groups a reviewer must belong to (any one); empty: no restriction.</param>
    Task<DeploymentRequestState> Submit(string bpmnXml, string requestedBy, string? comment,
        int requiredApprovals, bool deployDisabled, List<string> reviewerGroups);

    /// <param name="userGroups">The reviewer's groups, checked against the request's reviewer groups.</param>
    Task<DeploymentRequestState> Approve(string userId, List<string> userGroups, string? comment);
    Task<DeploymentRequestState> Reject(string userId, List<string> userGroups, string reason);
    Task<DeploymentRequestState> Withdraw(string userId);

    /// <summary>Deploys an approved request whose last deploy attempt failed.</summary>
    Task<DeploymentRequestState> RetryDeployment();

    /// <summary>The request, or <c>null</c> when nothing was submitted under this id.</summary>
    [ReadOnly]
    Task<DeploymentRequestState?> GetState();
}
