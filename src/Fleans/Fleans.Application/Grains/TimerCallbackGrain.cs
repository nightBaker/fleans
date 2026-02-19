using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class TimerCallbackGrain : Grain, ITimerCallbackGrain, IRemindable
{
    private const string ReminderName = "timer-callback";

    private readonly ILogger<TimerCallbackGrain> _logger;

    public TimerCallbackGrain(ILogger<TimerCallbackGrain> logger)
    {
        _logger = logger;
    }

    public async Task Activate(TimeSpan dueTime)
    {
        var (workflowInstanceId, _, timerActivityId) = ParseKey();
        LogActivating(workflowInstanceId, timerActivityId, dueTime);
        await this.RegisterOrUpdateReminder(ReminderName, dueTime, TimeSpan.FromMinutes(1));
    }

    public async Task Cancel()
    {
        var (workflowInstanceId, _, timerActivityId) = ParseKey();
        try
        {
            var reminder = await this.GetReminder(ReminderName);
            if (reminder != null)
            {
                await this.UnregisterReminder(reminder);
                LogCancelled(workflowInstanceId, timerActivityId);
            }
        }
        catch (Exception ex)
        {
            // Reminder may not exist — that's fine
            LogCancelFailed(workflowInstanceId, timerActivityId, ex);
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
            return;

        var (workflowInstanceId, hostActivityInstanceId, timerActivityId) = ParseKey();
        LogReminderFired(workflowInstanceId, timerActivityId);

        // Call back to WorkflowInstance first — if this fails, the periodic
        // reminder will fire again and retry. HandleTimerFired is idempotent
        // (stale-timer guards check if the activity is still active).
        var workflowInstance = GrainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
        await workflowInstance.HandleTimerFired(timerActivityId, hostActivityInstanceId);

        // Unregister only after successful callback
        try
        {
            var reminder = await this.GetReminder(ReminderName);
            if (reminder != null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            // If unregister fails, the next periodic tick will retry —
            // HandleTimerFired is idempotent so this is safe.
            LogReminderUnregisterFailed(workflowInstanceId, timerActivityId, ex);
        }
    }

    /// <summary>
    /// Parses the compound key. Guid = workflowInstanceId,
    /// string = "{hostActivityInstanceId}:{timerActivityId}".
    /// </summary>
    private (Guid WorkflowInstanceId, Guid HostActivityInstanceId, string TimerActivityId) ParseKey()
    {
        var workflowInstanceId = this.GetPrimaryKey(out var keyString);
        // hostActivityInstanceId Guid is always 36 chars, followed by ':'
        var hostActivityInstanceId = Guid.Parse(keyString!.AsSpan(0, 36));
        var timerActivityId = keyString[37..];
        return (workflowInstanceId, hostActivityInstanceId, timerActivityId);
    }

    [LoggerMessage(EventId = 10000, Level = LogLevel.Information,
        Message = "Timer callback activating for workflow {WorkflowInstanceId}, activity {TimerActivityId}, due in {DueTime}")]
    private partial void LogActivating(Guid workflowInstanceId, string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 10001, Level = LogLevel.Information,
        Message = "Timer callback cancelled for workflow {WorkflowInstanceId}, activity {TimerActivityId}")]
    private partial void LogCancelled(Guid workflowInstanceId, string timerActivityId);

    [LoggerMessage(EventId = 10002, Level = LogLevel.Information,
        Message = "Timer callback reminder fired for workflow {WorkflowInstanceId}, activity {TimerActivityId}")]
    private partial void LogReminderFired(Guid workflowInstanceId, string timerActivityId);

    [LoggerMessage(EventId = 10003, Level = LogLevel.Debug,
        Message = "Timer callback cancel failed for workflow {WorkflowInstanceId}, activity {TimerActivityId} — reminder may not exist")]
    private partial void LogCancelFailed(Guid workflowInstanceId, string timerActivityId, Exception exception);

    [LoggerMessage(EventId = 10004, Level = LogLevel.Debug,
        Message = "Timer callback reminder unregister failed for workflow {WorkflowInstanceId}, activity {TimerActivityId}")]
    private partial void LogReminderUnregisterFailed(Guid workflowInstanceId, string timerActivityId, Exception exception);
}
