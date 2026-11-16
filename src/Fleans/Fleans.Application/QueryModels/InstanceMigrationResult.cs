using Fleans.Domain.InstanceMigration;

namespace Fleans.Application.QueryModels;

/// <summary>What a migration (or its dry run) did to one instance (#1069).</summary>
[GenerateSerializer]
public enum InstanceMigrationOutcome
{
    /// <summary>The instance moved to the target version.</summary>
    Migrated,
    /// <summary>The same migration already moved it, or it already runs the target with the same mapping; nothing changed.</summary>
    AlreadyMigrated,
    /// <summary>The validator rejected it; nothing changed. <see cref="InstanceMigrationResult.Rejections"/> says why.</summary>
    Rejected,
    /// <summary>Dry run: the instance can move.</summary>
    Migratable,
    /// <summary>The call failed (e.g. the instance or the target version does not exist).</summary>
    Failed,
}

/// <param name="Rejections">Codes and element ids only — never variable values.</param>
[GenerateSerializer]
public sealed record InstanceMigrationResult(
    [property: Id(0)] Guid InstanceId,
    [property: Id(1)] InstanceMigrationOutcome Outcome,
    [property: Id(2)] string? SourceProcessDefinitionId,
    [property: Id(3)] string TargetProcessDefinitionId,
    [property: Id(4)] List<MigrationRejection> Rejections,
    [property: Id(5)] string? Error = null);
