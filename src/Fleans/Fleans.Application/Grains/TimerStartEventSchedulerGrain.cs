using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

[CorePlacement]
public partial class TimerStartEventSchedulerGrain : Grain, ITimerStartEventSchedulerGrain, IRemindable
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<TimerStartEventSchedulerGrain> _logger;
    private readonly IPersistentState<TimerStartEventSchedulerState> _state;

    private TimerStartEventSchedulerState State => _state.State;

    public TimerStartEventSchedulerGrain(
        [PersistentState("state", GrainStorageNames.TimerSchedulers)] IPersistentState<TimerStartEventSchedulerState> state,
        IGrainFactory grainFactory,
        ILogger<TimerStartEventSchedulerGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
    }

    // Orleans rejects reminder periods below ReminderOptions.MinimumReminderPeriod (1 min by
    // default), so the reminder period is never the BPMN cycle interval. Instead the reminder
    // is registered with dueTime = next fire and a fixed 1-minute period (the period only
    // acts as a retry tick if a fire fails); after each successful cycle fire it is
    // re-registered with dueTime = interval. Same pattern as TimerCallbackGrain.
    private static readonly TimeSpan ReminderRetryPeriod = TimeSpan.FromMinutes(1);

    public async Task ActivateScheduler(string processDefinitionId)
    {
        var timerStart = await GetTimerStartEvent()
            ?? throw new InvalidOperationException("Workflow does not have a TimerStartEvent");

        var dueTime = timerStart.TimerDefinition.GetDueTime();

        var maxFireCount = timerStart.TimerDefinition.Type == TimerType.Cycle
            ? timerStart.TimerDefinition.ParseCycle().RepeatCount
            : 1;
        State.Activate(processDefinitionId, maxFireCount);
        await this.RegisterOrUpdateReminder("timer-start", dueTime, ReminderRetryPeriod);

        await _state.WriteStateAsync();
        LogSchedulerActivated(this.GetPrimaryKeyString(), processDefinitionId);
    }

    public async Task DeactivateScheduler()
    {
        try
        {
            var reminder = await this.GetReminder("timer-start");
            if (reminder != null)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception ex)
        {
            LogReminderUnregisterFailed(this.GetPrimaryKeyString(), ex);
        }

        LogSchedulerDeactivated(this.GetPrimaryKeyString());
    }

    public async Task<Guid> FireTimerStartEvent()
    {
        var processKey = this.GetPrimaryKeyString();
        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(processKey);
        var definition = await processGrain.GetLatestDefinition();

        var timerStart = definition.Activities.OfType<TimerStartEvent>().FirstOrDefault();
        var childId = Guid.NewGuid();
        var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(childId);
        await child.SetWorkflow(definition, timerStart?.ActivityId);
        await child.StartWorkflow();

        State.IncrementFireCount();
        await _state.WriteStateAsync();
        LogTimerStartEventFired(processKey, childId, State.FireCount);

        return childId;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != "timer-start")
            return;

        await FireTimerStartEvent();

        if (State.MaxFireCount.HasValue && State.FireCount >= State.MaxFireCount.Value)
        {
            await DeactivateScheduler();
            return;
        }

        // Cycle timer with repetitions left: arm the next fire one interval from now.
        var timerStart = await GetTimerStartEvent();
        if (timerStart?.TimerDefinition.Type == TimerType.Cycle)
        {
            var interval = timerStart.TimerDefinition.ParseCycle().Interval;
            await this.RegisterOrUpdateReminder("timer-start", interval, ReminderRetryPeriod);
            LogCycleReArmed(this.GetPrimaryKeyString(), interval, State.FireCount);
        }
    }

    private async Task<TimerStartEvent?> GetTimerStartEvent()
    {
        var processGrain = _grainFactory.GetGrain<IProcessDefinitionGrain>(this.GetPrimaryKeyString());
        var definition = await processGrain.GetLatestDefinition();
        return definition.Activities.OfType<TimerStartEvent>().FirstOrDefault();
    }

    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "Timer start event scheduler activated for process {ProcessKey}, definition {ProcessDefinitionId}")]
    private partial void LogSchedulerActivated(string processKey, string processDefinitionId);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Timer start event scheduler deactivated for process {ProcessKey}")]
    private partial void LogSchedulerDeactivated(string processKey);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "Timer start event fired for process {ProcessKey}, created instance {InstanceId} (fire #{FireCount})")]
    private partial void LogTimerStartEventFired(string processKey, Guid instanceId, int fireCount);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Warning, Message = "Failed to unregister timer-start reminder for process {ProcessKey}")]
    private partial void LogReminderUnregisterFailed(string processKey, Exception ex);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Information, Message = "Timer start cycle re-armed for process {ProcessKey}: next fire in {Interval} (fired {FireCount} so far)")]
    private partial void LogCycleReArmed(string processKey, TimeSpan interval, int fireCount);
}
