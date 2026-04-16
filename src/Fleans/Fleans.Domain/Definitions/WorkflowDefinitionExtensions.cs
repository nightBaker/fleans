using Fleans.Domain.Activities;

namespace Fleans.Domain;

public static class WorkflowDefinitionExtensions
{
    /// <summary>
    /// Recursively searches all nested scope containers for the given activityId.
    /// INVARIANT: all scope-bearing activities (SubProcess, Transaction, EventSubProcess)
    /// must implement IWorkflowDefinition. If a new scope type is added without implementing
    /// IWorkflowDefinition, its children will be silently skipped here.
    /// </summary>
    public static Activity? FindActivityRecursive(this IWorkflowDefinition definition, string activityId)
    {
        foreach (var activity in definition.Activities)
        {
            if (activity.ActivityId == activityId) return activity;
            if (activity is IWorkflowDefinition nested)
            {
                var found = nested.FindActivityRecursive(activityId);
                if (found is not null) return found;
            }
        }
        return null;
    }
}
