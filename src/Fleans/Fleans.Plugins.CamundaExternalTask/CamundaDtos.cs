namespace Fleans.Plugins.CamundaExternalTask;

// Request/response shapes of the Camunda 7 REST API (/engine-rest/external-task). Property names
// serialize camelCase, matching Camunda. Request properties a Fleans deployment cannot honour
// (businessKey, tenantId, variable filters, …) are accepted and ignored by the JSON binder.

/// <summary>Camunda typed value: <c>{ "value": …, "type": "String", "valueInfo": {} }</c>.</summary>
public sealed record VariableValueDto(object? Value, string? Type, Dictionary<string, object?>? ValueInfo);

public sealed record FetchExternalTasksDto(
    string? WorkerId,
    int MaxTasks,
    bool UsePriority,
    long? AsyncResponseTimeout,
    List<FetchExternalTaskTopicDto>? Topics);

public sealed record FetchExternalTaskTopicDto(
    string TopicName,
    long LockDuration,
    List<string>? Variables,
    bool LocalVariables,
    string? ProcessDefinitionId,
    List<string>? ProcessDefinitionIdIn,
    string? ProcessDefinitionKey,
    List<string>? ProcessDefinitionKeyIn);

public sealed record CompleteExternalTaskDto(
    string? WorkerId,
    Dictionary<string, VariableValueDto>? Variables,
    Dictionary<string, VariableValueDto>? LocalVariables);

public sealed record ExternalTaskFailureDto(
    string? WorkerId,
    string? ErrorMessage,
    string? ErrorDetails,
    int? Retries,
    long RetryTimeout);

public sealed record ExternalTaskBpmnErrorDto(
    string? WorkerId,
    string? ErrorCode,
    string? ErrorMessage,
    Dictionary<string, VariableValueDto>? Variables);

public sealed record ExtendLockDto(string? WorkerId, long NewDuration);

public sealed record LockExternalTaskDto(string? WorkerId, long LockDuration);

public sealed record RetriesDto(int? Retries);

public sealed record PriorityDto(long Priority);

public sealed record CountResultDto(long Count);

public sealed record CamundaErrorDto(string Type, string Message);

/// <summary>Response of <c>GET /external-task/{id}</c> and list queries.</summary>
public record ExternalTaskDto(
    string Id,
    string TopicName,
    string? WorkerId,
    string? LockExpirationTime,
    string? CreateTime,
    string ActivityId,
    string ActivityInstanceId,
    string ExecutionId,
    string ProcessInstanceId,
    string? ProcessDefinitionId,
    string ProcessDefinitionKey,
    string? ProcessDefinitionVersionTag,
    int? Retries,
    string? ErrorMessage,
    bool Suspended,
    string? TenantId,
    long Priority,
    string? BusinessKey);

/// <summary>Element of the <c>fetchAndLock</c> response.</summary>
public sealed record LockedExternalTaskDto(
    string Id,
    string TopicName,
    string WorkerId,
    string LockExpirationTime,
    string? CreateTime,
    string ActivityId,
    string ActivityInstanceId,
    string ExecutionId,
    string ProcessInstanceId,
    string? ProcessDefinitionId,
    string ProcessDefinitionKey,
    string? ProcessDefinitionVersionTag,
    int? Retries,
    string? ErrorMessage,
    string? ErrorDetails,
    string? TenantId,
    long Priority,
    string? BusinessKey,
    Dictionary<string, VariableValueDto> Variables,
    Dictionary<string, string> ExtensionProperties);
