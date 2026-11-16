using Fleans.Application.QueryModels;
using Fleans.Domain.InstanceMigration;

namespace Fleans.Application.InstanceMigration;

/// <summary>
/// Which instances to move and where (#1069). Give either <see cref="InstanceIds"/> or
/// <see cref="SourceProcessDefinitionId"/> ("all running instances of that version").
/// </summary>
/// <param name="RequestId">
/// Identifies the request: each instance's migration id is <c>{RequestId}:{instanceId}</c>, so a
/// retried request is a no-op for the instances it already moved. Generated when omitted.
/// </param>
/// <param name="ContinuationToken">From the previous page of a <see cref="SourceProcessDefinitionId"/> request.</param>
public sealed record InstanceMigrationRequest(
    IReadOnlyList<Guid>? InstanceIds,
    string? SourceProcessDefinitionId,
    string TargetProcessDefinitionId,
    IReadOnlyList<ActivityMapping>? Mappings = null,
    bool UpdateEventTriggers = false,
    bool DryRun = false,
    string? RequestId = null,
    string? ContinuationToken = null);

/// <param name="ContinuationToken">Set when more running instances of the source version are left; pass it back for the next page.</param>
public sealed record InstanceMigrationBatchResult(
    string RequestId,
    bool DryRun,
    IReadOnlyList<InstanceMigrationResult> Results,
    string? ContinuationToken);

/// <summary>The default (identity) mapping between two versions of a process, for the mapping grid.</summary>
public sealed record InstanceMigrationPreview(
    string ProcessDefinitionKey,
    string SourceProcessDefinitionId,
    int SourceVersion,
    string TargetProcessDefinitionId,
    int TargetVersion,
    int RunningInstanceCount,
    IReadOnlyList<MigrationPreviewElement> Elements,
    IReadOnlyList<MigrationTargetElement> TargetElements);

/// <summary>An element of the target version a source element can be mapped to.</summary>
public sealed record MigrationTargetElement(string ActivityId, string ActivityType);

/// <summary>Moves running instances to another version of their process, one instance or a batch (#1069).</summary>
public interface IInstanceMigrationService
{
    /// <summary>Migrates (or, with <see cref="InstanceMigrationRequest.DryRun"/>, validates) each instance and reports per instance.</summary>
    Task<InstanceMigrationBatchResult> MigrateAsync(InstanceMigrationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Migrates one instance; the result says whether it moved and why not.</summary>
    Task<InstanceMigrationResult> MigrateInstanceAsync(Guid instanceId, InstanceMigrationRequest request);

    /// <summary>The identity mapping from <paramref name="fromVersion"/> to <paramref name="toVersion"/>; null when either is not deployed.</summary>
    Task<InstanceMigrationPreview?> PreviewAsync(string processDefinitionKey, int fromVersion, int toVersion);
}
