using Fleans.Application.QueryModels;
using Microsoft.JSInterop;

namespace Fleans.Web.Components.Pages;

/// <summary>
/// Paints an instance's state on the instance page's diagram (<c>bpmnViewer</c>): run states plus
/// the path taken. Shared by the live view and the debugger, which paints the state as of a journal step.
/// </summary>
internal static class InstanceDiagram
{
    public static async Task HighlightAsync(IJSRuntime js, InstanceStateSnapshot snapshot)
    {
        var all = snapshot.CompletedActivities.Concat(snapshot.ActiveActivities).ToList();
        var errorIds = all.Where(a => a.ErrorState != null).Select(a => a.ActivityId).ToArray();
        var cancelledIds = all.Where(a => a.IsCancelled).Select(a => a.ActivityId).ToArray();
        var lastActivity = all
            .OrderByDescending(a => a.CompletedAt ?? a.ExecutionStartedAt ?? a.CreatedAt)
            .FirstOrDefault();
        var lastErrorId = lastActivity?.ErrorState != null ? lastActivity.ActivityId : null;
        // Done = completed cleanly and not running again (a loop re-enters the same element).
        var completedIds = all
            .Where(a => a.IsCompleted && a.ErrorState == null && !a.IsCancelled)
            .Select(a => a.ActivityId)
            .Except(snapshot.ActiveActivityIds)
            .Except(errorIds)
            .Distinct()
            .ToArray();
        await js.InvokeVoidAsync("bpmnViewer.highlight", snapshot.ActiveActivityIds, errorIds, lastErrorId, cancelledIds, completedIds);

        await HighlightPathAsync(js, all, a => a.IsCompleted && a.ErrorState == null && !a.IsCancelled);
    }

    /// <summary>
    /// Marks the sequence flows the run took: from an element that completed to one created after it
    /// (bpmnViewer.highlightExecutionPath).
    /// </summary>
    /// <param name="activities">The activity instances that have started.</param>
    /// <param name="completedOk">Whether an instance completed successfully, so its token moved on.</param>
    public static async Task HighlightPathAsync(
        IJSRuntime js, IEnumerable<ActivityInstanceSnapshot> activities, Func<ActivityInstanceSnapshot, bool> completedOk)
    {
        var steps = activities.Select(a => new
        {
            activityId = a.ActivityId,
            createdAt = Millis(a.CreatedAt),
            doneAt = completedOk(a) ? Millis(a.CompletedAt) : null
        }).ToArray();
        await js.InvokeVoidAsync("bpmnViewer.highlightExecutionPath", (object)steps);
    }

    // Fractional milliseconds: events of one append can be microseconds apart.
    private static double? Millis(DateTimeOffset? at) => (at - DateTimeOffset.UnixEpoch)?.TotalMilliseconds;
}
