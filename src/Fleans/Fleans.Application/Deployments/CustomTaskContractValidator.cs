using Fleans.Application.CustomTasks;
using Fleans.Application.ExternalTasks;
using Fleans.Domain;
using Fleans.Domain.Activities;

namespace Fleans.Application.Deployments;

/// <summary>
/// Checks a deployment's custom-task inputs against the plugin version each task selects (#1051).
/// A version with no connected worker can't be checked, so it gets a warning and the deploy goes on.
/// A task type served by a connected wildcard, like the Camunda bridge, has no contract to check.
/// Versions already deployed, and their instances, are not rechecked.
/// </summary>
public static class CustomTaskContractValidator
{
    /// <summary>
    /// The deployment's warnings. Throws an <see cref="ArgumentException"/> naming each violation.
    /// </summary>
    public static async Task<List<string>> Check(IGrainFactory grainFactory, IWorkflowDefinition workflow)
    {
        var tasks = CustomTaskAccessValidator.CollectTasks(workflow);
        if (tasks.Count == 0)
            return [];

        var catalog = await grainFactory.GetGrain<ICustomTaskCatalogGrain>(0).GetAll();
        var warnings = new List<string>();
        var violations = new List<string>();
        foreach (var task in tasks)
        {
            var entry = catalog.FirstOrDefault(e => string.Equals(e.TaskType, task.TaskType, StringComparison.OrdinalIgnoreCase));
            var versions = entry?.Versions ?? [];
            if (versions.Count == 0 && await HasWildcardWorker(grainFactory, task.TaskType))
                continue;

            var selected = CustomTaskVersions.SelectGroup(versions, v => v.Version, task.TaskVersion).FirstOrDefault();
            if (selected is null)
            {
                warnings.Add($"Task '{task.ActivityId}': no worker of custom task '{task.TaskType}' " +
                    $"({task.TaskVersion ?? CustomTaskVersions.Latest}) is connected, so its inputs were not checked.");
                continue;
            }
            if (selected.SchemaConflict)
            {
                warnings.Add($"Task '{task.ActivityId}': the workers of custom task '{task.TaskType}' declare no version " +
                    "and differ in their parameters, so its inputs were not checked.");
                continue;
            }
            if (selected.ParameterSchema is { } schema)
            {
                violations.AddRange(CustomTaskInputRules.CheckSources(schema, task.InputMappings)
                    .Select(violation => $"Task '{task.ActivityId}' ({task.TaskType} {selected.Version ?? "without a version"}): {violation}"));
            }
        }

        if (violations.Count > 0)
            throw new ArgumentException($"The custom-task inputs don't fit their plugins. {string.Join("; ", violations)}.");
        return warnings;
    }

    private static async Task<bool> HasWildcardWorker(IGrainFactory grainFactory, string taskType) =>
        ExternalTaskWorkerPoolGrain.WildcardKeyFor(taskType) is { } wildcard
        && (await grainFactory.GetGrain<IExternalTaskWorkerPoolGrain>(wildcard).GetWorkers()).Count > 0;
}
