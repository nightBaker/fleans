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
/// A script task in a format other than C# gets a warning when no worker of its
/// <c>fleans.script.&lt;format&gt;</c> task type is connected (#1105).
/// </summary>
public static class CustomTaskContractValidator
{
    /// <summary>
    /// The deployment's warnings. Throws an <see cref="ArgumentException"/> naming each violation.
    /// </summary>
    public static async Task<List<string>> Check(IGrainFactory grainFactory, IWorkflowDefinition workflow)
    {
        var tasks = CustomTaskAccessValidator.CollectTasks(workflow);
        var scripts = CustomTaskAccessValidator.Collect<ScriptTask>(workflow).Where(s => !s.IsBuiltIn).ToList();
        if (tasks.Count == 0 && scripts.Count == 0)
            return [];

        var catalog = await grainFactory.GetGrain<ICustomTaskCatalogGrain>(0).GetAll();
        var warnings = new List<string>();

        // A script format runs on the worker of fleans.script.<format> (#1105). Without one connected
        // the task waits until one connects, so the deploy only warns: the worker may come later.
        foreach (var script in scripts)
        {
            var taskType = ScriptFormats.ToTaskType(script.ScriptFormat);
            var entry = catalog.FirstOrDefault(e => string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.SiloNames.Count == 0)
                warnings.Add($"Script task '{script.ActivityId}': no worker for script format '{script.ScriptFormat}' " +
                    $"({taskType}) is connected, so the task will wait until one connects.");
        }

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
