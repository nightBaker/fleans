using System.Dynamic;
using Fleans.Application.Placement;
using Fleans.Application.StartEvents;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

/// <summary>
/// Makes a message send with an idempotency key happen once (#743). Not reentrant, so concurrent
/// sends of one key queue: the first does the work, the rest return its recorded outcome. The start
/// ids are persisted before the first start (<see cref="MessageDeduplicationState"/>), so a send
/// interrupted mid-way resumes on retry with the same instances instead of starting new ones.
/// </summary>
[CorePlacement]
public partial class MessageDeduplicationGrain : Grain, IMessageDeduplicationGrain
{
    private readonly IPersistentState<MessageDeduplicationState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly StartEventLauncher _startEvents;
    private readonly TimeProvider _time;
    private readonly ILogger<MessageDeduplicationGrain> _logger;

    public MessageDeduplicationGrain(
        [PersistentState("state", GrainStorageNames.MessageDeduplications)]
        IPersistentState<MessageDeduplicationState> state,
        IGrainFactory grainFactory,
        StartEventLauncher startEvents,
        TimeProvider time,
        ILogger<MessageDeduplicationGrain> logger)
    {
        _state = state;
        _grainFactory = grainFactory;
        _startEvents = startEvents;
        _time = time;
        _logger = logger;
    }

    private MessageDeduplicationState Record => _state.State;

    public async Task<SendMessageResult> Send(string? correlationKey, ExpandoObject variables, string? businessId)
    {
        var key = this.GetPrimaryKeyString();
        var messageName = MessageDeduplicationKey.MessageNameOf(key);
        var now = _time.GetUtcNow();

        if (Record.IsRecorded(now))
        {
            switch (Record.Status)
            {
                case MessageDeduplicationStatus.Correlated:
                    LogDuplicateAnswered(key, Record.Status, Record.ReceivedAt);
                    return new SendMessageResult(Delivered: true);
                case MessageDeduplicationStatus.Started:
                    LogDuplicateAnswered(key, Record.Status, Record.ReceivedAt);
                    return new SendMessageResult(Delivered: true, WorkflowInstanceIds: Record.StartedInstanceIds.ToList());
                case MessageDeduplicationStatus.Starting:
                    // An earlier send stopped between its plan and its last write (crash, timeout):
                    // run the same plan again — instances it already started ignore the repeat.
                    LogResumingStarts(key, Record.PlannedStarts.Count);
                    return await StartPlanned(key, messageName, variables, businessId);
            }
        }

        if (await MessageDelivery.TryCorrelate(_grainFactory, messageName, correlationKey, variables))
        {
            Record.RecordCorrelated(now);
            await _state.WriteStateAsync();
            LogCorrelatedRecorded(key);
            return new SendMessageResult(Delivered: true);
        }

        var plan = await _startEvents.PlanMessageStarts(messageName);
        if (plan.Count == 0)
            return new SendMessageResult(Delivered: false); // nothing happened, nothing to record

        Record.PlanStarts(plan, now);
        await _state.WriteStateAsync();
        LogStartsPlanned(key, plan.Count);

        return await StartPlanned(key, messageName, variables, businessId);
    }

    private async Task<SendMessageResult> StartPlanned(
        string key, string messageName, ExpandoObject variables, string? businessId)
    {
        var started = await _startEvents.StartPlanned(messageName, Record.PlannedStarts, variables, businessId);

        Record.CompleteStarts(started);
        await _state.WriteStateAsync();

        if (started.Count == 0)
        {
            LogNothingStarted(key);
            return new SendMessageResult(Delivered: false);
        }

        LogStartsRecorded(key, started.Count);
        return new SendMessageResult(Delivered: true, WorkflowInstanceIds: started);
    }

    [LoggerMessage(EventId = 14400, Level = LogLevel.Information,
        Message = "Message {DeduplicationKey} was already sent at {ReceivedAt} ({Status}); returned that outcome instead of sending it again")]
    private partial void LogDuplicateAnswered(string deduplicationKey, MessageDeduplicationStatus status, DateTimeOffset receivedAt);

    [LoggerMessage(EventId = 14401, Level = LogLevel.Debug,
        Message = "Message {DeduplicationKey} delivered to a running instance; recorded")]
    private partial void LogCorrelatedRecorded(string deduplicationKey);

    [LoggerMessage(EventId = 14402, Level = LogLevel.Debug,
        Message = "Message {DeduplicationKey} will start {ProcessCount} process(es); instance ids recorded")]
    private partial void LogStartsPlanned(string deduplicationKey, int processCount);

    [LoggerMessage(EventId = 14403, Level = LogLevel.Debug,
        Message = "Message {DeduplicationKey} started {InstanceCount} instance(s); recorded")]
    private partial void LogStartsRecorded(string deduplicationKey, int instanceCount);

    [LoggerMessage(EventId = 14404, Level = LogLevel.Warning,
        Message = "Message {DeduplicationKey} was interrupted while starting {ProcessCount} process(es); resuming with the recorded instance ids")]
    private partial void LogResumingStarts(string deduplicationKey, int processCount);

    [LoggerMessage(EventId = 14405, Level = LogLevel.Debug,
        Message = "Message {DeduplicationKey} started no instance; record dropped, so a retry sends it again")]
    private partial void LogNothingStarted(string deduplicationKey);
}
