using Microsoft.Extensions.Logging;

namespace Fleans.Application.Grains;

public partial class WorkflowInstance
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Workflow definition set")]
    private partial void LogWorkflowDefinitionSet();

    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Workflow execution started")]
    private partial void LogWorkflowStarted();

    [LoggerMessage(EventId = 1078, Level = LogLevel.Information,
        Message = "Root-scope event sub-process listeners armed: {ListenerCount}")]
    private partial void LogRootScopeListenersArmed(int listenerCount);

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

    [LoggerMessage(EventId = 1014, Level = LogLevel.Information, Message = "Child workflow completed for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowCompleted(string parentActivityId);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning, Message = "Child workflow failed for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowFailed(string parentActivityId);

    [LoggerMessage(EventId = 1020, Level = LogLevel.Information, Message = "Child workflow failed with no boundary handler, propagating error to parent. ParentActivityId={ParentActivityId}")]
    private partial void LogChildFailurePropagatedToParent(string parentActivityId);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Information, Message = "Timer reminder fired for activity {TimerActivityId}")]
    private partial void LogTimerReminderFired(string timerActivityId);

    [LoggerMessage(EventId = 1024, Level = LogLevel.Debug, Message = "Stale timer ignored for activity {TimerActivityId} — activity no longer active")]
    private partial void LogStaleTimerIgnored(string timerActivityId);

    [LoggerMessage(EventId = 1025, Level = LogLevel.Information, Message = "Message delivered as boundary event for activity {ActivityId}")]
    private partial void LogMessageDeliveryBoundary(string activityId);

    [LoggerMessage(EventId = 1026, Level = LogLevel.Information, Message = "Message delivered, completing activity {ActivityId}")]
    private partial void LogMessageDeliveryComplete(string activityId);

    [LoggerMessage(EventId = 1027, Level = LogLevel.Debug, Message = "Join gateway {ActivityId} already active ({ActivityInstanceId}), reusing entry instead of creating duplicate")]
    private partial void LogJoinGatewayDeduplication(string activityId, Guid activityInstanceId);

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

    [LoggerMessage(EventId = 1040, Level = LogLevel.Information,
        Message = "Sub-process {ActivityId} child scope variables merged into parent scope {ParentScopeId}")]
    private partial void LogSubProcessVariablesMerged(string activityId, Guid parentScopeId);

    [LoggerMessage(EventId = 1039, Level = LogLevel.Information,
        Message = "Scope child {ActivityId} cancelled (scope {ScopeId})")]
    private partial void LogScopeChildCancelled(string activityId, Guid scopeId);

    [LoggerMessage(EventId = 1041, Level = LogLevel.Information,
        Message = "Multi-instance scope completed for activity {ActivityId}")]
    private partial void LogMultiInstanceScopeCompleted(string activityId);

    [LoggerMessage(EventId = 1042, Level = LogLevel.Debug,
        Message = "Multi-instance sequential: spawned next iteration {Index} for activity {ActivityId}")]
    private partial void LogMultiInstanceNextIteration(int index, string activityId);

    [LoggerMessage(EventId = 1043, Level = LogLevel.Warning,
        Message = "Multi-instance host failed for activity {ActivityId}: {ErrorMessage}")]
    private partial void LogMultiInstanceHostFailed(string activityId, string errorMessage);

    [LoggerMessage(EventId = 1044, Level = LogLevel.Debug,
        Message = "Multi-instance parallel: spawned iteration {Index} for activity {ActivityId}")]
    private partial void LogMultiInstanceIterationSpawned(int index, string activityId);

    [LoggerMessage(EventId = 1045, Level = LogLevel.Debug,
        Message = "Multi-instance output aggregated for activity {ActivityId}: {OutputCollection} ({Count} items)")]
    private partial void LogMultiInstanceOutputAggregated(string activityId, string outputCollection, int count);

    [LoggerMessage(EventId = 1046, Level = LogLevel.Debug,
        Message = "Stale callback ignored for activity {ActivityId} ({ActivityInstanceId}) — {CallbackType} arrived after entry was already completed")]
    private partial void LogStaleCallbackIgnored(string activityId, Guid activityInstanceId, string callbackType);

    [LoggerMessage(EventId = 1047, Level = LogLevel.Information,
        Message = "Inclusive fork '{ActivityId}': created token {TokenId} for branch")]
    private partial void LogTokenCreated(string activityId, Guid tokenId);

    [LoggerMessage(EventId = 1048, Level = LogLevel.Debug,
        Message = "Token {TokenId} inherited by activity '{ActivityId}'")]
    private partial void LogTokenInherited(Guid tokenId, string activityId);

    [LoggerMessage(EventId = 1049, Level = LogLevel.Information,
        Message = "Token {TokenId} restored after join '{ActivityId}' (from fork {ForkInstanceId})")]
    private partial void LogTokenRestored(Guid tokenId, string activityId, Guid forkInstanceId);

    [LoggerMessage(EventId = 1050, Level = LogLevel.Information,
        Message = "Gateway fork state created: forkInstanceId={ForkInstanceId}, consumedToken={ConsumedTokenId}")]
    private partial void LogGatewayForkStateCreated(Guid forkInstanceId, Guid? consumedTokenId);

    [LoggerMessage(EventId = 1051, Level = LogLevel.Information,
        Message = "Gateway fork state removed: forkInstanceId={ForkInstanceId}")]
    private partial void LogGatewayForkStateRemoved(Guid forkInstanceId);

    [LoggerMessage(EventId = 1052, Level = LogLevel.Warning,
        Message = "Stale message delivery ignored for activityId='{ActivityId}', hostActivityInstanceId={HostActivityInstanceId} — activity already completed")]
    private partial void LogStaleMessageDeliveryIgnored(string activityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 3007, Level = LogLevel.Information, Message = "Workflow started: InstanceId={InstanceId}")]
    private partial void LogWorkflowInstanceStarted(Guid instanceId);

    [LoggerMessage(EventId = 3008, Level = LogLevel.Debug, Message = "Activity execution started: {ActivityInstanceId}")]
    private partial void LogActivityExecutionStarted(Guid activityInstanceId);

    [LoggerMessage(EventId = 3009, Level = LogLevel.Debug, Message = "Child variable scope created: ScopeId={ScopeId}, ParentScopeId={ParentScopeId}")]
    private partial void LogChildVariableScopeCreated(Guid scopeId, Guid parentScopeId);

    [LoggerMessage(EventId = 3010, Level = LogLevel.Debug, Message = "Variable scope cloned: NewScopeId={NewScopeId}, SourceScopeId={SourceScopeId}")]
    private partial void LogVariableScopeCloned(Guid newScopeId, Guid sourceScopeId);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Debug, Message = "Variable scopes removed: Count={Count}")]
    private partial void LogVariableScopesRemoved(int count);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Debug, Message = "Condition sequences added: GatewayInstanceId={GatewayInstanceId}, Count={Count}")]
    private partial void LogConditionSequencesAdded(Guid gatewayInstanceId, int count);

    [LoggerMessage(EventId = 3013, Level = LogLevel.Debug, Message = "Condition sequence evaluated: GatewayInstanceId={GatewayInstanceId}, SequenceFlowId={SequenceFlowId}, Result={Result}")]
    private partial void LogConditionSequenceEvaluated(Guid gatewayInstanceId, string sequenceFlowId, bool result);

    [LoggerMessage(EventId = 3014, Level = LogLevel.Debug, Message = "Gateway fork token added: ForkInstanceId={ForkInstanceId}, TokenId={TokenId}")]
    private partial void LogGatewayForkTokenAdded(Guid forkInstanceId, Guid tokenId);

    [LoggerMessage(EventId = 3015, Level = LogLevel.Debug, Message = "Multi-instance total set: ActivityInstanceId={ActivityInstanceId}, Total={Total}")]
    private partial void LogMultiInstanceTotalSet(Guid activityInstanceId, int total);

    [LoggerMessage(EventId = 3016, Level = LogLevel.Debug,
        Message = "Timer cycle re-registration deferred to TimerCallbackGrain for activity {TimerActivityId}, dueTime={DueTime}")]
    private partial void LogTimerCycleReRegistrationDeferred(string timerActivityId, TimeSpan dueTime);

    [LoggerMessage(EventId = 3020, Level = LogLevel.Debug,
        Message = "Activity spawned: ActivityInstanceId={ActivityInstanceId}, ActivityId={ActivityId}, ActivityType={ActivityType}")]
    private partial void LogActivitySpawned(Guid activityInstanceId, string activityId, string activityType);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Debug,
        Message = "Timer cycle updated: HostActivityInstanceId={HostActivityInstanceId}, TimerActivityId={TimerActivityId}, HasRemainingCycle={HasRemainingCycle}")]
    private partial void LogTimerCycleUpdated(Guid hostActivityInstanceId, string timerActivityId, bool hasRemainingCycle);

    [LoggerMessage(EventId = 3017, Level = LogLevel.Information, Message = "Workflow execution started")]
    private partial void LogExecutionStarted();

    [LoggerMessage(EventId = 3018, Level = LogLevel.Debug,
        Message = "Activity execution reset (join gateway deduplication): ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogActivityExecutionReset(Guid activityInstanceId);

    [LoggerMessage(EventId = 3019, Level = LogLevel.Information,
        Message = "Child workflow linked: ActivityInstanceId={ActivityInstanceId}, ChildWorkflowInstanceId={ChildWorkflowInstanceId}")]
    private partial void LogChildWorkflowLinked(Guid activityInstanceId, Guid childWorkflowInstanceId);

    // User task lifecycle (EventId 1060-1069)
    [LoggerMessage(EventId = 1060, Level = LogLevel.Information,
        Message = "User task claim attempt: ActivityInstanceId={ActivityInstanceId}, UserId={UserId}")]
    private partial void LogUserTaskClaimAttempt(Guid activityInstanceId, string userId);

    [LoggerMessage(EventId = 1061, Level = LogLevel.Information,
        Message = "User task unclaim attempt: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskUnclaimAttempt(Guid activityInstanceId);

    [LoggerMessage(EventId = 1062, Level = LogLevel.Information,
        Message = "User task complete attempt: ActivityInstanceId={ActivityInstanceId}, UserId={UserId}")]
    private partial void LogUserTaskCompleteAttempt(Guid activityInstanceId, string userId);

    // User task domain events (EventId 3024-3029)
    [LoggerMessage(EventId = 3024, Level = LogLevel.Information,
        Message = "User task registered: ActivityInstanceId={ActivityInstanceId}, Assignee={Assignee}")]
    private partial void LogUserTaskRegistered(Guid activityInstanceId, string? assignee);

    [LoggerMessage(EventId = 3025, Level = LogLevel.Information,
        Message = "User task claimed: ActivityInstanceId={ActivityInstanceId}, UserId={UserId}")]
    private partial void LogUserTaskClaimed(Guid activityInstanceId, string userId);

    [LoggerMessage(EventId = 3026, Level = LogLevel.Information,
        Message = "User task unclaimed: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskUnclaimed(Guid activityInstanceId);

    [LoggerMessage(EventId = 3027, Level = LogLevel.Information,
        Message = "User task unregistered: ActivityInstanceId={ActivityInstanceId}")]
    private partial void LogUserTaskUnregistered(Guid activityInstanceId);

    // Pending external event queue (EventId 1070-1079)
    [LoggerMessage(EventId = 1070, Level = LogLevel.Debug,
        Message = "Child workflow completed queued for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowCompletedQueued(string parentActivityId);

    [LoggerMessage(EventId = 1071, Level = LogLevel.Debug,
        Message = "Child workflow failed queued for CallActivity {ParentActivityId}")]
    private partial void LogChildWorkflowFailedQueued(string parentActivityId);

    [LoggerMessage(EventId = 1072, Level = LogLevel.Debug,
        Message = "Signal delivery queued for activity {ActivityId}, hostInstanceId={HostActivityInstanceId}")]
    private partial void LogSignalDeliveryQueued(string activityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 1073, Level = LogLevel.Debug,
        Message = "Boundary signal fired queued for activity {BoundaryActivityId}, hostInstanceId={HostActivityInstanceId}")]
    private partial void LogBoundarySignalFiredQueued(string boundaryActivityId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 1074, Level = LogLevel.Debug,
        Message = "Processing pending external event: {EventType}")]
    private partial void LogProcessingPendingEvent(string eventType);

    // Event Sub-Process lifecycle (EventId 1075-1079)
    [LoggerMessage(EventId = 1075, Level = LogLevel.Information,
        Message = "Workflow completed via root EventSubProcess {EventSubProcessId} (no outgoing sequence flow)")]
    private partial void LogRootEventSubProcessCompleted(string eventSubProcessId);

    [LoggerMessage(EventId = 1076, Level = LogLevel.Debug,
        Message = "EventSubProcess host completed: {EventSubProcessId} hostInstance={HostActivityInstanceId}")]
    private partial void LogEventSubProcessHostCompleted(
        string eventSubProcessId, Guid hostActivityInstanceId);

    [LoggerMessage(EventId = 1077, Level = LogLevel.Debug,
        Message = "EventSubProcess handler EndEvent fired: esp={EventSubProcessId} endEvent={EndEventId} hostInstance={HostActivityInstanceId}")]
    private partial void LogEventSubProcessHandlerEndEventFired(
        string eventSubProcessId, string endEventId, Guid hostActivityInstanceId);

    // Escalation lifecycle (EventId 1090-1099)
    [LoggerMessage(EventId = 1090, Level = LogLevel.Information,
        Message = "Escalation thrown: EscalationCode={EscalationCode}, ActivityId={ActivityId}, WorkflowInstanceId={WorkflowInstanceId}")]
    private partial void LogEscalationThrown(string escalationCode, string activityId, Guid workflowInstanceId);

    [LoggerMessage(EventId = 1091, Level = LogLevel.Debug,
        Message = "Child escalation raised queued: ChildId={ChildWorkflowInstanceId}, EscalationCode={EscalationCode}")]
    private partial void LogChildEscalationRaisedQueued(Guid childWorkflowInstanceId, string escalationCode);

    [LoggerMessage(EventId = 1092, Level = LogLevel.Information,
        Message = "Escalation caught: EscalationCode={EscalationCode}, BoundaryActivityId={BoundaryActivityId}, IsInterrupting={IsInterrupting}")]
    private partial void LogEscalationCaught(string? escalationCode, string boundaryActivityId, bool isInterrupting);

    [LoggerMessage(EventId = 1093, Level = LogLevel.Information,
        Message = "Child escalation raised: ChildId={ChildWorkflowInstanceId}, EscalationCode={EscalationCode}")]
    private partial void LogChildEscalationRaised(Guid childWorkflowInstanceId, string escalationCode);

    [LoggerMessage(EventId = 3030, Level = LogLevel.Warning,
        Message = "Workflow cancelled: {Reason}")]
    private partial void LogWorkflowCancelled(string reason);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Warning,
        Message = "Escalation uncaught: EscalationCode={EscalationCode}, SourceActivityId={SourceActivityId}")]
    private partial void LogEscalationUncaught(string escalationCode, string sourceActivityId);

    [LoggerMessage(EventId = 3032, Level = LogLevel.Warning,
        Message = "Escalation parent result missing: expected _pendingEscalationParentResult to be set for EscalationCode={EscalationCode}, WorkflowInstanceId={WorkflowInstanceId} — defaulting to Unhandled")]
    private partial void LogEscalationParentResultMissing(string? escalationCode, Guid workflowInstanceId);

    // Compensation event lifecycle (EventId 1110-1119)
    [LoggerMessage(EventId = 1110, Level = LogLevel.Debug,
        Message = "Compensable activity snapshot recorded: ActivityDefinitionId={ActivityDefinitionId}, Sequence={Sequence}, ScopeId={ScopeId}")]
    private partial void LogCompensableActivitySnapshotRecorded(string activityDefinitionId, int sequence, Guid? scopeId);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Information,
        Message = "Compensation walk started: ScopeId={ScopeId}, TargetActivityRef={TargetActivityRef}, HandlerCount={HandlerCount}")]
    private partial void LogCompensationWalkStarted(Guid? scopeId, string? targetActivityRef, int handlerCount);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Information,
        Message = "Compensation handler spawned: HandlerInstanceId={HandlerInstanceId}, CompensableActivityDefinitionId={CompensableActivityDefinitionId}, HandlerActivityId={HandlerActivityId}")]
    private partial void LogCompensationHandlerSpawned(Guid handlerInstanceId, string compensableActivityDefinitionId, string handlerActivityId);

    [LoggerMessage(EventId = 1113, Level = LogLevel.Information,
        Message = "Compensation entry marked compensated: ActivityDefinitionId={ActivityDefinitionId}, ScopeId={ScopeId}")]
    private partial void LogCompensationEntryMarkedCompensated(string activityDefinitionId, Guid? scopeId);

    [LoggerMessage(EventId = 1114, Level = LogLevel.Information,
        Message = "Compensation walk completed: ScopeId={ScopeId}")]
    private partial void LogCompensationWalkCompleted(Guid? scopeId);

    [LoggerMessage(EventId = 1115, Level = LogLevel.Information,
        Message = "Compensation handler completed: HandlerInstanceId={HandlerInstanceId}, CompensableActivityDefinitionId={CompensableActivityDefinitionId}")]
    private partial void LogCompensationHandlerCompleted(Guid handlerInstanceId, string compensableActivityDefinitionId);

    [LoggerMessage(EventId = 1116, Level = LogLevel.Error,
        Message = "Compensation handler failed: HandlerInstanceId={HandlerInstanceId}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}")]
    private partial void LogCompensationHandlerFailed(Guid handlerInstanceId, int errorCode, string errorMessage);

    // Complex gateway lifecycle (EventId 1080-1089)
    [LoggerMessage(EventId = 1080, Level = LogLevel.Debug,
        Message = "Complex gateway activation condition late callback — join instance {ActivityInstanceId} has already fired or was removed")]
    private partial void LogComplexGatewayActivationConditionLateCallback(Guid activityInstanceId);

    [LoggerMessage(EventId = 1081, Level = LogLevel.Debug,
        Message = "Complex gateway join {ActivityInstanceId} waiting for more tokens: count={TokenCount}, condition={Condition}")]
    private partial void LogComplexGatewayWaitingForMoreTokens(Guid activityInstanceId, int tokenCount, string condition);

    [LoggerMessage(EventId = 1082, Level = LogLevel.Information,
        Message = "Complex gateway join {ActivityInstanceId} activation condition met: {Condition} (tokenCount={TokenCount})")]
    private partial void LogComplexGatewayActivationConditionMet(string condition, Guid activityInstanceId, int tokenCount);

    [LoggerMessage(EventId = 1083, Level = LogLevel.Information,
        Message = "CompleteActivationCondition called for activity {ActivityId}, instance {ActivityInstanceId}, result={Result}")]
    private partial void LogCompleteActivationCondition(string activityId, Guid activityInstanceId, bool result);

    // Multiple event (EventId 1090-1093)
    [LoggerMessage(EventId = 1090, Level = LogLevel.Information,
        Message = "Multiple catch event registered: activity {ActivityId}, {DefinitionCount} watchers")]
    private partial void LogMultipleCatchRegistered(string activityId, int definitionCount);

    [LoggerMessage(EventId = 1091, Level = LogLevel.Information,
        Message = "Multiple catch event fired: activity {ActivityId}, trigger type {TriggerType}")]
    private partial void LogMultipleCatchFired(string activityId, string triggerType);

    [LoggerMessage(EventId = 1092, Level = LogLevel.Information,
        Message = "Multiple throw event fired: activity {ActivityId}, {DefinitionCount} definitions thrown")]
    private partial void LogMultipleThrowFired(string activityId, int definitionCount);

    [LoggerMessage(EventId = 1093, Level = LogLevel.Information,
        Message = "Multiple boundary event fired: activity {ActivityId}, trigger type {TriggerType}, interrupting={IsInterrupting}")]
    private partial void LogMultipleBoundaryFired(string activityId, string triggerType, bool isInterrupting);

    // Conditional event watchers (EventId 1094-1097)
    [LoggerMessage(EventId = 1094, Level = LogLevel.Information,
        Message = "Conditional watcher registered: expression={ConditionExpression}, activityId={ActivityId}")]
    private partial void LogConditionalWatcherRegistered(string conditionExpression, string activityId);

    [LoggerMessage(EventId = 1095, Level = LogLevel.Information,
        Message = "Conditional watcher fired: expression={ConditionExpression}, activityId={ActivityId}")]
    private partial void LogConditionalWatcherFired(string conditionExpression, string activityId);

    [LoggerMessage(EventId = 1096, Level = LogLevel.Information,
        Message = "Conditional watcher cleared: activityInstanceId={ActivityInstanceId}")]
    private partial void LogConditionalWatcherCleared(Guid activityInstanceId);

    [LoggerMessage(EventId = 1097, Level = LogLevel.Warning,
        Message = "Conditional expression evaluation failed: expression={ConditionExpression}, activityId={ActivityId}, error={ErrorMessage}")]
    private partial void LogConditionalExpressionEvaluationFailed(string conditionExpression, string activityId, string errorMessage);

    // ── Transaction Sub-Process lifecycle (EventId range 1100–1103; 1101–1102 reserved for Phase 2) ──

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug,
        Message = "Transaction scope opened: activityId={ActivityId} instanceId={TransactionInstanceId} parentScopeId={ParentScopeId}")]
    private partial void LogTransactionScopeOpened(string activityId, Guid transactionInstanceId, Guid? parentScopeId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information,
        Message = "Transaction cancel initiated: transactionInstanceId={TransactionInstanceId} activityId={ActivityId}")]
    private partial void LogTransactionCancelInitiated(Guid transactionInstanceId, string activityId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information,
        Message = "Transaction cancel boundary taken: transactionInstanceId={TransactionInstanceId} activityId={ActivityId}")]
    private partial void LogTransactionCancelBoundaryTaken(Guid transactionInstanceId, string activityId);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information,
        Message = "Transaction completed: transactionInstanceId={TransactionInstanceId} activityId={ActivityId}")]
    private partial void LogTransactionCompleted(Guid transactionInstanceId, string activityId);

}
