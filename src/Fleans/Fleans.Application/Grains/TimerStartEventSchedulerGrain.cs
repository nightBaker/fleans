using Fleans.Application.WorkflowFactory;
using Fleans.Domain.Activities;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.Grains;

public partial class TimerStartEventSchedulerGrain : Grain, ITimerStartEventSchedulerGrain, IRemindable
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<TimerStartEventSchedulerGrain> _logger;
    private string? _processDefinitionId;
    private int _fireCount;
    private int? _maxFireCount;

    public TimerStartEventSchedulerGrain(
        IGrainFactory grainFactory,
        ILogger<TimerStartEventSchedulerGrain> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public async Task ActivateScheduler(string processDefinitionId)
    {
        _processDefinitionId = processDefinitionId;

        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var definition = await factory.GetLatestWorkflowDefinition(this.GetPrimaryKeyString());
        var timerStart = definition.Activities.OfType<TimerStartEvent>().FirstOrDefault()
            ?? throw new InvalidOperationException("Workflow does not have a TimerStartEvent");

        var dueTime = timerStart.TimerDefinition.GetDueTime();

        if (timerStart.TimerDefinition.Type == TimerType.Cycle)
        {
            var (repeatCount, interval) = timerStart.TimerDefinition.ParseCycle();
            _maxFireCount = repeatCount;
            await this.RegisterOrUpdateReminder("timer-start", dueTime, interval);
        }
        else
        {
            _maxFireCount = 1;
            await this.RegisterOrUpdateReminder("timer-start", dueTime, TimeSpan.FromMinutes(1));
        }

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
        catch
        {
            // Reminder may not exist — that's fine
        }

        LogSchedulerDeactivated(this.GetPrimaryKeyString());
    }

    public async Task<Guid> FireTimerStartEvent()
    {
        var processKey = this.GetPrimaryKeyString();
        var factory = _grainFactory.GetGrain<IWorkflowInstanceFactoryGrain>(0);
        var definition = await factory.GetLatestWorkflowDefinition(processKey);

        var childId = Guid.NewGuid();
        var child = _grainFactory.GetGrain<IWorkflowInstanceGrain>(childId);
        await child.SetWorkflow(definition);
        await child.StartWorkflow();

        _fireCount++;
        LogTimerStartEventFired(processKey, childId, _fireCount);

        return childId;
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != "timer-start")
            return;

        await FireTimerStartEvent();

        if (_maxFireCount.HasValue && _fireCount >= _maxFireCount.Value)
        {
            await DeactivateScheduler();
        }
    }

    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "Timer start event scheduler activated for process {ProcessKey}, definition {ProcessDefinitionId}")]
    private partial void LogSchedulerActivated(string processKey, string processDefinitionId);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Timer start event scheduler deactivated for process {ProcessKey}")]
    private partial void LogSchedulerDeactivated(string processKey);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "Timer start event fired for process {ProcessKey}, created instance {InstanceId} (fire #{FireCount})")]
    private partial void LogTimerStartEventFired(string processKey, Guid instanceId, int fireCount);
}
