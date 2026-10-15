using Fleans.Application.Placement;
using Fleans.Domain;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

/// <summary>
/// Stateless and non-reentrant: Orleans runs one call at a time per key, so two starts with
/// the same (process, business id) cannot both pass the check. The check reads the
/// WorkflowInstances projection, which the instance writes before <c>SetWorkflow</c> returns,
/// so the next queued call already sees the instance this call created (#824).
/// </summary>
[CorePlacement]
public partial class BusinessIdGuardGrain : Grain, IBusinessIdGuardGrain
{
    private readonly IWorkflowQueryService _queryService;
    private readonly ILogger<BusinessIdGuardGrain> _logger;

    public BusinessIdGuardGrain(IWorkflowQueryService queryService, ILogger<BusinessIdGuardGrain> logger)
    {
        _queryService = queryService;
        _logger = logger;
    }

    public async Task SetWorkflowIfUnique(
        IWorkflowInstanceGrain instance, IWorkflowDefinition workflow, string? startActivityId)
    {
        var (processDefinitionKey, businessId) = BusinessIdGuardKey.Parse(this.GetPrimaryKeyString());

        var existing = await _queryService.FindActiveRootInstanceByBusinessId(processDefinitionKey, businessId);
        if (existing is Guid existingId)
        {
            LogBusinessIdConflict(processDefinitionKey, businessId, existingId);
            throw new InvalidOperationException(
                $"An active instance of process '{processDefinitionKey}' with BusinessId '{businessId}' already exists ({existingId}).");
        }

        await instance.SetWorkflow(workflow, startActivityId, businessId);
    }

    [LoggerMessage(EventId = 13000, Level = LogLevel.Warning,
        Message = "Start rejected: process {ProcessDefinitionKey} already has active instance {ExistingInstanceId} with BusinessId {BusinessId}")]
    private partial void LogBusinessIdConflict(string processDefinitionKey, string businessId, Guid existingInstanceId);
}
