using System.Dynamic;
using Orleans.Concurrency;

namespace Fleans.Application.Grains;

/// <summary>
/// Narrow callback surface that plugin-side custom-task handlers use to talk back to a
/// workflow instance grain. Lives in Fleans.Application.Abstractions so Fleans.Worker
/// (the plugin-author scaffolding) can depend on it without pulling Fleans.Application.
/// The full grain interface — IWorkflowInstanceGrain — extends this one.
/// </summary>
public interface IWorkflowInstanceCallback : IGrainWithGuidKey
{
    [ReadOnly]
    ValueTask<ExpandoObject> GetVariables(Guid variablesStateId);

    /// <summary>
    /// Merged variables for <paramref name="variablesStateId"/> while the activity instance is
    /// still awaiting completion; <c>null</c> once it has completed or failed. Handlers call this
    /// before running side effects so a redelivered (or duplicate) event for an already-finished
    /// activity is skipped instead of executed twice — in one round-trip with the variable read.
    /// </summary>
    [ReadOnly]
    ValueTask<ExpandoObject?> GetVariablesIfActive(Guid activityInstanceId, Guid variablesStateId);

    Task CompleteActivity(string activityId, Guid activityInstanceId, ExpandoObject variables);

    Task FailActivity(string activityId, Guid activityInstanceId, Exception exception);
}
