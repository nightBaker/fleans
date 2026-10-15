using Fleans.Domain;

namespace Fleans.Application.Grains;

/// <summary>
/// Serializes starts that share one (process definition key, business id) pair so the
/// "no other active root instance carries this business id" check and the instance
/// creation run as one step (#824). Keyed by <see cref="BusinessIdGuardKey"/>.
/// Used only when <see cref="BusinessIdOptions.EnforceUniqueness"/> is on.
/// </summary>
public interface IBusinessIdGuardGrain : IGrainWithStringKey
{
    /// <summary>
    /// Calls <see cref="IWorkflowInstanceGrain.SetWorkflow"/> on <paramref name="instance"/>
    /// with this grain's business id, or throws <see cref="InvalidOperationException"/>
    /// (HTTP 409) while another non-terminal root instance of the same process carries it.
    /// </summary>
    Task SetWorkflowIfUnique(IWorkflowInstanceGrain instance, IWorkflowDefinition workflow, string? startActivityId);
}

public static class BusinessIdGuardKey
{
    // A BPMN process id is an xsd:ID (NCName) and cannot contain ':', so the first ':'
    // always separates the key from the business id, which may itself contain ':'.
    public static string Build(string processDefinitionKey, string businessId)
        => $"{processDefinitionKey}:{businessId}";

    public static (string ProcessDefinitionKey, string BusinessId) Parse(string grainKey)
    {
        var separator = grainKey.IndexOf(':');
        if (separator <= 0)
            throw new ArgumentException($"Invalid business id guard key '{grainKey}'.", nameof(grainKey));
        return (grainKey[..separator], grainKey[(separator + 1)..]);
    }
}
