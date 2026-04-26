namespace Fleans.Application.QueryModels;

public record MessageStartEventEntry(string MessageName, string ProcessDefinitionKey);
public record SignalStartEventEntry(string SignalName, string ProcessDefinitionKey);
public record ConditionalStartEventEntry(string ProcessDefinitionKey, string ActivityId, string ConditionExpression);
public record ActiveMessageSubscriptionEntry(string MessageName, string CorrelationKey, Guid WorkflowInstanceId, string ActivityId);
public record ActiveSignalSubscriptionEntry(string SignalName, Guid WorkflowInstanceId, string ActivityId);

public record RegisteredEventsInfo(
    IReadOnlyList<MessageStartEventEntry> MessageStartEvents,
    IReadOnlyList<SignalStartEventEntry> SignalStartEvents,
    IReadOnlyList<ConditionalStartEventEntry> ConditionalStartEvents,
    IReadOnlyList<ActiveMessageSubscriptionEntry> ActiveMessageSubscriptions,
    IReadOnlyList<ActiveSignalSubscriptionEntry> ActiveSignalSubscriptions);
