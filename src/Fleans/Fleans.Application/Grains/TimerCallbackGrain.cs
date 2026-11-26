using Fleans.Application.Observability;
using Fleans.Application.Placement;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[CorePlacement]
public partial class TimerCallbackGrain : Grain, ITimerCallbackGrain, IRemindable
{
    private const string ReminderName = "timer-callback";
    // A delivery that failed retries under this name, registered at the first failure, so its
    // FirstTickTime marks when the failures started rather than when the timer fell due (#854).
    private const string RetryReminderName = "timer-callback-retry";

    private readonly TimerDeliveryLimiter _deliveryLimiter;
    private readonly ILogger<TimerCallbackGrain> _logger;
    public TimerCallbackGrain(TimerDeliveryLimiter deliveryLimiter, ILogger<TimerCallbackGrain> logger)
    {
        _deliveryLimiter = deliveryLimiter;
        _logger = logger;
    }

    public async Task Activate(TimeSpan dueTime)
    {
        var (workflowInstanceId, _, timerActivityId) = ParseKey();
        LogActivating(workflowInstanceId, timerActivityId, dueTime);
        await this.RegisterOrUpdateReminder(ReminderName, dueTime, TimeSpan.FromMinutes(1));
        // The grain holds no state; the reminder reactivates it when the timer falls due. Leaving it
        // to Orleans' 15-minute idle collection kept every recently armed timer in silo memory (#1066).
        DeactivateOnIdle();
    }

    public async Task Cancel()
    {
        var (workflowInstanceId, _, timerActivityId) = ParseKey();
        foreach (var name in new[] { ReminderName, RetryReminderName })
        {
            try
            {
                var reminder = await this.GetReminder(name);
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
        DeactivateOnIdle();
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName is not (ReminderName or RetryReminderName))
            return;

        var (workflowInstanceId, hostActivityInstanceId, timerActivityId) = ParseKey();
        try
        {
            await DeliverTick(workflowInstanceId, hostActivityInstanceId, timerActivityId, reminderName, status);
        }
        finally
        {
            // Deferred, delivered or failed, nothing is pending: the reminder reactivates the grain
            // for its next tick, so don't hold the activation for Orleans' 15-minute idle collection.
            DeactivateOnIdle();
        }
    }

    private async Task DeliverTick(
        Guid workflowInstanceId, Guid hostActivityInstanceId, string timerActivityId, string reminderName, TickStatus status)
    {
        // Bounded delivery (#1066): Orleans fires every due reminder at once, so a burst above the
        // engine's fire rate would reactivate that many instances concurrently. Without a free slot
        // the tick is skipped, not queued: the reminder stays registered and its 1-minute period
        // ticks again, so the burst becomes lag. Waiting here instead would hold this non-reentrant
        // grain — an instance's Cancel() would queue behind the wait and then block the very
        // HandleTimerFired call the wait ends in — and would outlast the reminder call's timeout.
        if (!_deliveryLimiter.TryEnter())
        {
            LogDeliveryDeferred(workflowInstanceId, timerActivityId);
            FleansDiagnostics.TimerDeliveriesDeferred.Add(1);
            if (_deliveryLimiter.TryClaimSaturationWarning())
                LogDeliverySaturated(_deliveryLimiter.Limit);
            return;
        }

        LogReminderFired(workflowInstanceId, timerActivityId);

        // Call back to WorkflowInstance first — if this fails, the periodic
        // reminder will fire again and retry. HandleTimerFired is idempotent
        // (stale-timer guards check if the activity is still active).
        // Returns non-null TimeSpan when a cycle timer needs re-registration.
        TimeSpan? cycleReRegistration;
        try
        {
            var workflowInstance = GrainFactory.GetGrain<IWorkflowInstanceGrain>(workflowInstanceId);
            cycleReRegistration = await workflowInstance.HandleTimerFired(timerActivityId, hostActivityInstanceId);
        }
        catch (Exception ex)
        {
            _deliveryLimiter.Exit();
            if (!await TryHandleDeliveryFailure(workflowInstanceId, timerActivityId, reminderName, status, ex))
                throw;
            return;
        }
        _deliveryLimiter.Exit();

        // Each registration is due once, at FirstTickTime (registration + due time); the 1-minute
        // period only retries a failed delivery, so a retried timer's lag includes the retries.
        FleansDiagnostics.RecordTimerLag(status.FirstTickTime, status.CurrentTickTime);

        // Unregister the current reminder first, then re-register if needed.
        // This ordering prevents the unregister-race: we always unregister the
        // old reminder before creating a new one for the next cycle.
        try
        {
            var reminder = await this.GetReminder(reminderName);
            if (reminder != null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            // If unregister fails, the next periodic tick will retry —
            // HandleTimerFired is idempotent so this is safe.
            LogReminderUnregisterFailed(workflowInstanceId, timerActivityId, ex);
        }

        // Re-register locally for cycle timers (no cross-grain call — avoids deadlock)
        if (cycleReRegistration.HasValue)
        {
            LogCycleReRegistering(workflowInstanceId, timerActivityId, cycleReRegistration.Value);
            await this.RegisterOrUpdateReminder(ReminderName, cycleReRegistration.Value, TimeSpan.FromMinutes(1));
        }
    }

    // False when the failure should propagate: the retry reminder ticks again in a minute.
    private async Task<bool> TryHandleDeliveryFailure(
        Guid workflowInstanceId, string timerActivityId, string reminderName, TickStatus status, Exception ex)
    {
        if (reminderName == ReminderName)
        {
            // First failure: move the retries to the retry reminder, whose FirstTickTime then
            // dates the failures. Time the tick spent deferred or lagging doesn't count (#854).
            LogDeliveryFailedRetrying(workflowInstanceId, timerActivityId, ex);
            await this.RegisterOrUpdateReminder(RetryReminderName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            var reminder = await this.GetReminder(ReminderName);
            if (reminder != null)
                await this.UnregisterReminder(reminder);
            return true;
        }

        var failingFor = status.CurrentTickTime - status.FirstTickTime + TimeSpan.FromMinutes(1);
        if (failingFor <= GiveUpAfter)
            return false;

        // An instance that keeps failing the delivery (one that can no longer activate, for
        // example) would otherwise be retried every minute forever (#854).
        LogDeliveryAbandoned(workflowInstanceId, timerActivityId, failingFor, ex);
        await Cancel();
        return true;
    }

    /// <summary>How long a failing delivery is retried (once a minute) before the reminder is dropped.</summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(1);

    [LoggerMessage(EventId = 10009, Level = LogLevel.Warning,
        Message = "Timer delivery failed for workflow {WorkflowInstanceId}, activity {TimerActivityId}; retrying every minute for up to a day")]
    private partial void LogDeliveryFailedRetrying(Guid workflowInstanceId, string timerActivityId, Exception exception);

    [LoggerMessage(EventId = 10008, Level = LogLevel.Error,
        Message = "Timer delivery abandoned for workflow {WorkflowInstanceId}, activity {TimerActivityId}: it failed for {FailingFor}; the reminder is removed")]
    private partial void LogDeliveryAbandoned(Guid workflowInstanceId, string timerActivityId, TimeSpan failingFor, Exception exception);

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

    [LoggerMessage(EventId = 10005, Level = LogLevel.Information,
        Message = "Cycle timer re-registering locally for workflow {WorkflowInstanceId}, activity {TimerActivityId}, due in {DueTime}")]
    private partial void LogCycleReRegistering(Guid workflowInstanceId, string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 10006, Level = LogLevel.Debug,
        Message = "Timer delivery deferred for workflow {WorkflowInstanceId}, activity {TimerActivityId}: every timer delivery slot of this silo is busy (Fleans:Timers:DeliveryConcurrency); the reminder ticks again in a minute")]
    private partial void LogDeliveryDeferred(Guid workflowInstanceId, string timerActivityId);

    [LoggerMessage(EventId = 10007, Level = LogLevel.Warning,
        Message = "Every timer delivery slot of this silo is busy (Fleans:Timers:DeliveryConcurrency = {DeliveryConcurrency}): due timers are deferred a minute at a time. Logged at most once a minute; fleans.timer.deferred counts each deferred tick")]
    private partial void LogDeliverySaturated(int deliveryConcurrency);
}
