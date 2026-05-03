namespace Fleans.Application.QueryModels;

/// <summary>
/// Read-only snapshot for the admin UI <c>/events</c> page. Built once per
/// Refresh click; never serialized across an Orleans grain boundary so
/// <see cref="IReadOnlyList{T}"/> is the right collection type here. If this
/// record ever needs to flow through a grain call, the row records below
/// must switch to <see cref="List{T}"/> per CLAUDE.md (Orleans copier).
/// </summary>
public record RegisteredEventsSnapshot(
    IReadOnlyList<MessageStartEventRow> MessageStartEvents,
    IReadOnlyList<SignalStartEventRow> SignalStartEvents,
    IReadOnlyList<ConditionalStartEventRow> ConditionalStartEvents,
    IReadOnlyList<MessageSubscriptionRow> ActiveMessageSubscriptions,
    IReadOnlyList<SignalSubscriptionRow> ActiveSignalSubscriptions);

public record MessageStartEventRow(string MessageName, string ProcessDefinitionKey);

public record SignalStartEventRow(string SignalName, string ProcessDefinitionKey);

public record ConditionalStartEventRow(
    string ProcessDefinitionKey,
    string ActivityId,
    string ConditionExpression);

public record MessageSubscriptionRow(
    string MessageName,
    string CorrelationKey,
    Guid WorkflowInstanceId,
    string ActivityId,
    Guid ActivityInstanceId);

public record SignalSubscriptionRow(
    string SignalName,
    Guid WorkflowInstanceId,
    string ActivityId,
    Guid ActivityInstanceId);
