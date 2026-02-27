using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Workflow definition set")]
    private partial void LogWorkflowDefinitionSet();

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Workflow execution started")]
    private partial void LogWorkflowStarted();

    [LoggerMessage(EventId = 1002, Level = LogLevel.Debug, Message = "Executing activity {ActivityId} ({ActivityType})")]
    private partial void LogExecutingActivity(string activityId, string activityType);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Completing activity {ActivityId}")]
    private partial void LogCompletingActivity(string activityId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning, Message = "Failing activity {ActivityId}")]
    private partial void LogFailingActivity(string activityId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug, Message = "Condition sequence result: {SequenceFlowId}={Result}")]
    private partial void LogConditionResult(string sequenceFlowId, bool result);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug, Message = "Transitioning: {CompletedCount} completed, {NewCount} new")]
    private partial void LogTransition(int completedCount, int newCount);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Debug, Message = "Gateway {ActivityId} decision made, auto-completing and resuming workflow")]
    private partial void LogGatewayAutoCompleting(string activityId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug, Message = "Gateway {ActivityId} short-circuited: condition {ConditionSequenceFlowId} is true")]
    private partial void LogGatewayShortCircuited(string activityId, string conditionSequenceFlowId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Debug, Message = "Gateway {ActivityId} all conditions false, taking default flow")]
    private partial void LogGatewayTakingDefaultFlow(string activityId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Error, Message = "Gateway {ActivityId} all conditions false and no default flow — misconfigured workflow")]
    private partial void LogGatewayNoDefaultFlow(string activityId);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Workflow initialized with start activity {ActivityId}")]
    private partial void LogStateStartWith(string activityId);
    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "Workflow completed")]
    private partial void LogStateCompleted();

    [LoggerMessage(EventId = 3003, Level = LogLevel.Debug, Message = "Variables merged for state {VariablesId}")]
    private partial void LogStateMergeState(Guid variablesId);

    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "Adding {Count} entries")]
    private partial void LogStateAddEntries(int count);

    [LoggerMessage(EventId = 3005, Level = LogLevel.Debug, Message = "Completing {Count} activities")]
    private partial void LogStateCompleteEntries(int count);

    [LoggerMessage(EventId = 3006, Level = LogLevel.Debug, Message = "State persisted after transition")]
    private partial void LogStatePersistedAfterTransition();

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information, Message = "Parent info set: ParentWorkflowInstanceId={ParentWorkflowInstanceId}, ParentActivityId={ParentActivityId}")]
    private partial void LogParentInfoSet(Guid parentWorkflowInstanceId, string parentActivityId);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Debug, Message = "Initial variables set")]
    private partial void LogInitialVariablesSet();

    [LoggerMessage(EventId = 1013, Level = LogLevel.Information, Message = "Starting child workflow: CalledProcessKey={CalledProcessKey}, ChildId={ChildId}")]
    private partial void LogStartingChildWorkflow(string calledProcessKey, Guid childId);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "Child workflow completed for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowCompleted(string parentActivityId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "Child workflow failed for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowFailed(string parentActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Child workflow failed with no boundary handler, propagating error to parent. ParentActivityId={ParentActivityId}")]
    private partial void LogChildFailurePropagatedToParent(string parentActivityId);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Information, Message = "Timer reminder registered for activity {TimerActivityId}, due in {DueTime}")]
    private partial void LogTimerReminderRegistered(string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Information, Message = "Timer reminder fired for activity {TimerActivityId}")]
    private partial void LogTimerReminderFired(string timerActivityId);

    [LoggerMessage(EventId = 1021, Level = LogLevel.Information,
        Message = "Message subscription registered for activity {ActivityId}: messageName={MessageName}, correlationKey={CorrelationKey}")]
    private partial void LogMessageSubscriptionRegistered(string activityId, string messageName, string correlationKey);

    [LoggerMessage(EventId = 1023, Level = LogLevel.Warning,
        Message = "Message subscription failed for activity {ActivityId}: messageName={MessageName}, correlationKey={CorrelationKey}")]
    private partial void LogMessageSubscriptionFailed(string activityId, string messageName, string correlationKey, Exception exception);

    [LoggerMessage(EventId = 1024, Level = LogLevel.Debug, Message = "Stale timer ignored for activity {TimerActivityId} — activity no longer active")]
    private partial void LogStaleTimerIgnored(string timerActivityId);

    [LoggerMessage(EventId = 1025, Level = LogLevel.Information, Message = "Message delivered as boundary event for activity {ActivityId}")]
    private partial void LogMessageDeliveryBoundary(string activityId);

    [LoggerMessage(EventId = 1026, Level = LogLevel.Information, Message = "Message delivered, completing activity {ActivityId}")]
    private partial void LogMessageDeliveryComplete(string activityId);

    [LoggerMessage(EventId = 1027, Level = LogLevel.Debug, Message = "Join gateway {ActivityId} already active ({ActivityInstanceId}), reusing entry instead of creating duplicate")]
    private partial void LogJoinGatewayDeduplication(string activityId, Guid activityInstanceId);

    [LoggerMessage(EventId = 1028, Level = LogLevel.Information,
        Message = "Signal subscription registered for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionRegistered(string activityId, string signalName);

    [LoggerMessage(EventId = 1029, Level = LogLevel.Warning,
        Message = "Signal subscription failed for activity {ActivityId}: signalName={SignalName}")]
    private partial void LogSignalSubscriptionFailed(string activityId, string signalName, Exception exception);

    [LoggerMessage(EventId = 1030, Level = LogLevel.Information,
        Message = "Signal thrown: signalName={SignalName}, deliveredTo={DeliveredCount} subscribers")]
    private partial void LogSignalThrown(string signalName, int deliveredCount);

    [LoggerMessage(EventId = 1031, Level = LogLevel.Information, Message = "Signal delivered as boundary event for activity {ActivityId}")]
    private partial void LogSignalDeliveryBoundary(string activityId);

    [LoggerMessage(EventId = 1032, Level = LogLevel.Information, Message = "Signal delivered, completing activity {ActivityId}")]
    private partial void LogSignalDeliveryComplete(string activityId);

    [LoggerMessage(EventId = 1033, Level = LogLevel.Information,
        Message = "Event-based gateway: cancelled sibling {CancelledActivityId} because {WinningActivityId} completed first")]
    private partial void LogEventBasedGatewaySiblingCancelled(string cancelledActivityId, string winningActivityId);

    [LoggerMessage(EventId = 1037, Level = LogLevel.Information,
        Message = "Sub-process {ActivityId} initialized with child variable scope {ChildVariablesId}")]
    private partial void LogSubProcessInitialized(string activityId, Guid childVariablesId);

    [LoggerMessage(EventId = 1038, Level = LogLevel.Information,
        Message = "Sub-process {ActivityId} completed — all child activities done")]
    private partial void LogSubProcessCompleted(string activityId);

    [LoggerMessage(EventId = 1039, Level = LogLevel.Information,
        Message = "Scope child {ActivityId} cancelled (scope {ScopeId})")]
    private partial void LogScopeChildCancelled(string activityId, Guid scopeId);

    [LoggerMessage(EventId = 1040, Level = LogLevel.Information,
        Message = "Multi-instance scope opened for activity {ActivityId}: {IterationCount} iterations, sequential={IsSequential}")]
    private partial void LogMultiInstanceScopeOpened(string activityId, int iterationCount, bool isSequential);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Information,
        Message = "Multi-instance scope completed for activity {ActivityId}")]
    private partial void LogMultiInstanceScopeCompleted(string activityId);

    [LoggerMessage(EventId = 1042, Level = LogLevel.Debug,
        Message = "Multi-instance sequential: spawned next iteration {Index} for activity {ActivityId}")]
    private partial void LogMultiInstanceNextIteration(int index, string activityId);
}
