using System.Dynamic;
using Fleans.Application.Grains;
using Fleans.Domain;
using Microsoft.Extensions.Options;

namespace Fleans.Application;

/// <summary>
/// Runs the start sequence of a new instance (SetWorkflow, initial variables, StartWorkflow),
/// routed through <see cref="IBusinessIdGuardGrain"/> when business id uniqueness applies (#824).
/// </summary>
public sealed class WorkflowInstanceStarter
{
    private readonly IGrainFactory _grainFactory;
    private readonly IOptions<BusinessIdOptions> _options;

    public WorkflowInstanceStarter(IGrainFactory grainFactory, IOptions<BusinessIdOptions> options)
    {
        _grainFactory = grainFactory;
        _options = options;
    }

    /// <summary>
    /// Starts <paramref name="instance"/>. Returns <c>null</c> on success, or the id of the
    /// active instance that already holds <paramref name="businessId"/> when uniqueness
    /// rejected the start (nothing was created then).
    /// </summary>
    public Task<Guid?> Start(
        IWorkflowInstanceGrain instance,
        string processDefinitionKey,
        IWorkflowDefinition workflow,
        string? startActivityId,
        ExpandoObject? variables,
        string? businessId)
    {
        if (businessId is not null && _options.Value.EnforceUniqueness)
            return _grainFactory
                .GetGrain<IBusinessIdGuardGrain>(BusinessIdGuardKey.Build(processDefinitionKey, businessId))
                .StartIfUnique(instance, workflow, startActivityId, variables);

        return StartUnguarded(instance, workflow, startActivityId, variables, businessId);
    }

    internal static async Task<Guid?> StartUnguarded(
        IWorkflowInstanceGrain instance,
        IWorkflowDefinition workflow,
        string? startActivityId,
        ExpandoObject? variables,
        string? businessId)
    {
        await instance.SetWorkflow(workflow, startActivityId, businessId);
        if (variables is not null)
            await instance.SetInitialVariables(variables);
        await instance.StartWorkflow();
        return null;
    }

    public static string ConflictMessage(string processDefinitionKey, string businessId, Guid existingInstanceId) =>
        $"An active instance of process '{processDefinitionKey}' with BusinessId '{businessId}' already exists ({existingInstanceId}).";
}
