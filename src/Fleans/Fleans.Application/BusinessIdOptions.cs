using Fleans.Application.Grains;
using Fleans.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Fleans.Application;

/// <summary>
/// Business id settings (#824), bound from the <c>Fleans:BusinessId</c> configuration section.
/// Every core silo must use the same value.
/// </summary>
public sealed class BusinessIdOptions
{
    public const string SectionName = "Fleans:BusinessId";

    /// <summary>
    /// When true, a start that supplies a business id is rejected (HTTP 409) while another
    /// non-terminal root instance of the same process definition key (any version) carries it.
    /// Off by default, as in Camunda 8.
    /// </summary>
    public bool EnforceUniqueness { get; set; }
}

internal static class BusinessIdStart
{
    /// <summary>
    /// Initializes <paramref name="instance"/> with <paramref name="businessId"/>, going through
    /// <see cref="IBusinessIdGuardGrain"/> when uniqueness is enforced and a business id is set.
    /// </summary>
    public static Task SetWorkflow(
        IServiceProvider services,
        IGrainFactory grainFactory,
        IWorkflowInstanceGrain instance,
        string processDefinitionKey,
        IWorkflowDefinition workflow,
        string? startActivityId,
        string? businessId)
    {
        var enforce = services.GetRequiredService<IOptions<BusinessIdOptions>>().Value.EnforceUniqueness;
        if (businessId is null || !enforce)
            return instance.SetWorkflow(workflow, startActivityId, businessId);

        // Reject a malformed id before it becomes part of a grain key.
        BusinessIdRules.Validate(businessId);
        return grainFactory
            .GetGrain<IBusinessIdGuardGrain>(BusinessIdGuardKey.Build(processDefinitionKey, businessId))
            .SetWorkflowIfUnique(instance, workflow, startActivityId);
    }
}
