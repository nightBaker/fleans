namespace Fleans.Application.QueryModels;

// Rows for the admin UI /events page, one paged query per section (#754).
// Never serialized across an Orleans grain boundary, so they are read through
// PagedResult's IReadOnlyList. If they ever flow through a grain call, switch
// to List<T> per CLAUDE.md (Orleans copier).

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
