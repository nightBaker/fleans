namespace Fleans.ServiceDefaults.DTOs;

/// <summary>Maps an element of the instance's version to an element of the target version (#1069).</summary>
public record MigrationMappingDto(string SourceActivityId, string TargetActivityId);

/// <summary>
/// <c>POST /Instances/{id}/migrate</c> (#1069). Elements not listed in <see cref="Mappings"/> map to
/// the element with the same id. <see cref="UpdateEventTriggers"/> re-arms timers and subscriptions
/// whose definition changed (off: they keep their due time). <see cref="DryRun"/> only validates.
/// A retry with the same <see cref="RequestId"/> is a no-op once the instance moved.
/// </summary>
public record MigrateInstanceRequest(
    string? TargetProcessDefinitionId,
    List<MigrationMappingDto>? Mappings = null,
    bool UpdateEventTriggers = false,
    bool DryRun = false,
    string? RequestId = null);

/// <summary>
/// <c>POST /Instances/migrate</c> (#1069): either <see cref="InstanceIds"/> (at most
/// <c>Fleans:Migration:MaxBatchSize</c>) or <see cref="SourceProcessDefinitionId"/> (its running
/// instances, a page at a time — pass <see cref="ContinuationToken"/> from the previous response).
/// </summary>
public record MigrateInstancesRequest(
    List<Guid>? InstanceIds,
    string? SourceProcessDefinitionId,
    string? TargetProcessDefinitionId,
    List<MigrationMappingDto>? Mappings = null,
    bool UpdateEventTriggers = false,
    bool DryRun = false,
    string? RequestId = null,
    string? ContinuationToken = null);

/// <summary>Why an instance cannot move: a code (e.g. <c>unmapped-activity</c>) and the element it concerns.</summary>
public record MigrationRejectionDto(string Code, string? ActivityId, string Message);

/// <summary>
/// One instance's outcome: <c>Migrated</c>, <c>AlreadyMigrated</c>, <c>Rejected</c>,
/// <c>Migratable</c> (dry run) or <c>Failed</c>.
/// </summary>
public record InstanceMigrationResultDto(
    Guid InstanceId,
    string Outcome,
    string? SourceProcessDefinitionId,
    string TargetProcessDefinitionId,
    List<MigrationRejectionDto> Rejections,
    string? Error = null);

/// <param name="Outcomes">How many instances had each outcome.</param>
/// <param name="RejectionCodes">How many instances each rejection code rejected.</param>
/// <param name="ContinuationToken">Set when more running instances of the source version are left.</param>
public record MigrateInstancesResponse(
    string RequestId,
    bool DryRun,
    Dictionary<string, int> Outcomes,
    Dictionary<string, int> RejectionCodes,
    List<InstanceMigrationResultDto> Results,
    string? ContinuationToken);

/// <summary>One element of the source version and its default (same-id) mapping; <see cref="Problem"/> is a rejection code.</summary>
public record MigrationPreviewElementDto(
    string SourceActivityId, string SourceActivityType, string? TargetActivityId, string? TargetActivityType, string? Problem);

/// <summary><c>GET /Definitions/{key}/versions/{from}/migration-preview?to={to}</c> (#1069).</summary>
public record MigrationPreviewResponse(
    string ProcessDefinitionKey,
    string SourceProcessDefinitionId,
    int SourceVersion,
    string TargetProcessDefinitionId,
    int TargetVersion,
    int RunningInstanceCount,
    List<MigrationPreviewElementDto> Elements,
    List<MigrationTargetElementDto> TargetElements);

/// <summary>An element of the target version a source element can be mapped to.</summary>
public record MigrationTargetElementDto(string ActivityId, string ActivityType);
