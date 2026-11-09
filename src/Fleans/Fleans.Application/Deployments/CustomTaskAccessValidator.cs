using Fleans.Application.CustomTasks;
using Fleans.Domain;
using Fleans.Domain.Activities;

namespace Fleans.Application.Deployments;

/// <summary>
/// Rejects a deployment that uses a private custom-task plugin its process is not allowed to use
/// (#1022). Checked at deploy time only: versions already deployed, and their instances, are not
/// affected by later allow-list changes.
/// </summary>
public static class CustomTaskAccessValidator
{
    /// <summary>Throws an <see cref="ArgumentException"/> naming each disallowed task type.</summary>
    public static async Task EnsureAllowed(IGrainFactory grainFactory, string processKey, IWorkflowDefinition workflow)
    {
        var taskTypes = CollectTaskTypes(workflow);
        if (taskTypes.Count == 0)
            return;

        var disallowed = await grainFactory.GetGrain<ICustomTaskCatalogGrain>(0)
            .GetDisallowedTaskTypes(processKey, taskTypes);
        if (disallowed.Count > 0)
            throw new ArgumentException(
                $"Process '{processKey}' uses private custom task(s) it is not allowed to use: " +
                $"{string.Join(", ", disallowed.Select(t => $"'{t}'"))}. " +
                "Add the process to the plugin's allow-list (Custom Tasks page, or PUT /custom-tasks/{taskType}/allowed-processes).");
    }

    /// <summary>Every custom-task type in the definition, including sub-processes and multi-instance tasks.</summary>
    public static List<string> CollectTaskTypes(IWorkflowDefinition workflow) =>
        CollectTasks(workflow).Select(t => t.TaskType).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every custom task in the definition, including sub-processes and multi-instance tasks.</summary>
    public static List<CustomTaskActivity> CollectTasks(IWorkflowDefinition workflow)
    {
        var tasks = new List<CustomTaskActivity>();
        CollectFromScope(workflow, tasks);
        return tasks;
    }

    private static void CollectFromScope(IWorkflowDefinition scope, List<CustomTaskActivity> tasks)
    {
        foreach (var activity in scope.Activities)
            CollectFromActivity(activity, tasks);
    }

    private static void CollectFromActivity(Activity activity, List<CustomTaskActivity> tasks)
    {
        switch (activity)
        {
            case CustomTaskActivity task:
                tasks.Add(task);
                break;
            case MultiInstanceActivity multiInstance:
                CollectFromActivity(multiInstance.InnerActivity, tasks);
                break;
        }

        if (activity is IWorkflowDefinition nested)
            CollectFromScope(nested, tasks);
    }
}
