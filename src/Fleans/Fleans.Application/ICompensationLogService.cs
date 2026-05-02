using Fleans.Application.QueryModels;

namespace Fleans.Application;

/// <summary>
/// Returns the compensation log for a workflow instance — one entry per completed
/// compensable activity, annotated with the BPMN handler that compensates it.
/// </summary>
/// <remarks>
/// IMPORTANT: each call activates the workflow grain. This service is intended for
/// the admin UI's instance-detail page (one user-initiated fetch per page render).
/// Do NOT invoke from list views, analytics endpoints, or the
/// <c>GET /Workflow/instances/{id}/state</c> diagnostics endpoint, which read from
/// the eventually-consistent EF projection. The compensation log is intentionally
/// not in that projection — see <see cref="Fleans.Domain.States.WorkflowInstanceState.CompensationLog"/>.
/// </remarks>
public interface ICompensationLogService
{
    Task<IReadOnlyList<CompensationLogEntrySnapshot>> GetCompensationLog(Guid workflowInstanceId);
}
