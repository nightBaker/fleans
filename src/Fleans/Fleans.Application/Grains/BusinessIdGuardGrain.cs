using System.Dynamic;
using Fleans.Application.Placement;
using Fleans.Domain;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

/// <summary>
/// Stateless and non-reentrant: Orleans runs one call at a time per key, so two starts with
/// the same (process, business id) cannot both pass the check. The whole start runs inside
/// the call, and the instance projects itself to the primary store before each grain call
/// returns, so the next queued call sees it (#824).
/// Only <em>started</em> instances hold the id: a start that fails before execution begins
/// leaves an unstarted instance that does not block retries.
/// </summary>
// One activation per distinct business id, typically used once: collect it quickly rather than
// after the default idle age. Not DeactivateOnIdle() — calls queued behind the current one would
// be forwarded to a deactivating activation and rejected under a burst of same-key starts.
[CollectionAgeLimit(Minutes = 2)]
[CorePlacement]
public partial class BusinessIdGuardGrain : Grain, IBusinessIdGuardGrain
{
    private readonly IBusinessIdLookup _lookup;
    private readonly ILogger<BusinessIdGuardGrain> _logger;

    public BusinessIdGuardGrain(IBusinessIdLookup lookup, ILogger<BusinessIdGuardGrain> logger)
    {
        _lookup = lookup;
        _logger = logger;
    }

    public async Task<Guid?> StartIfUnique(
        IWorkflowInstanceGrain instance, IWorkflowDefinition workflow, string? startActivityId, ExpandoObject? variables)
    {
        var (processDefinitionKey, businessId) = BusinessIdGuardKey.Parse(this.GetPrimaryKeyString());

        var existing = await _lookup.FindActiveRootInstance(processDefinitionKey, businessId);
        if (existing is Guid existingId)
        {
            LogBusinessIdConflict(processDefinitionKey, businessId, existingId);
            return existingId;
        }

        return await WorkflowInstanceStarter.StartUnguarded(instance, workflow, startActivityId, variables, businessId);
    }

    [LoggerMessage(EventId = 13000, Level = LogLevel.Warning,
        Message = "Start rejected: process {ProcessDefinitionKey} already has active instance {ExistingInstanceId} with BusinessId {BusinessId}")]
    private partial void LogBusinessIdConflict(string processDefinitionKey, string businessId, Guid existingInstanceId);
}
