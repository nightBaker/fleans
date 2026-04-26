namespace Fleans.Application.QueryModels;

public record RegisteredEventsSnapshot(
    IReadOnlyList<MessageStartEventInfo> MessageStartEvents,
    IReadOnlyList<SignalStartEventInfo> SignalStartEvents,
    IReadOnlyList<MessageSubscriptionInfo> MessageSubscriptions,
    IReadOnlyList<SignalSubscriptionInfo> SignalSubscriptions,
    IReadOnlyList<ConditionalStartEventInfo> ConditionalStartEvents);

public record MessageStartEventInfo(string MessageName, string ProcessDefinitionKey);

public record SignalStartEventInfo(string SignalName, string ProcessDefinitionKey);

public record MessageSubscriptionInfo(
    string MessageName,
    string CorrelationKey,
    Guid WorkflowInstanceId,
    string ActivityId);

public record SignalSubscriptionInfo(
    string SignalName,
    Guid WorkflowInstanceId,
    string ActivityId);

public record ConditionalStartEventInfo(
    string ProcessDefinitionKey,
    string ActivityId,
    string ConditionExpression);
