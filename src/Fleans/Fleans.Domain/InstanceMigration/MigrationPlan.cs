namespace Fleans.Domain.InstanceMigration;

/// <summary>
/// Maps an element of the instance's current version to an element of the target version (#1069).
/// Elements without a pair map to the element with the same id (identity mapping).
/// </summary>
[GenerateSerializer]
public record ActivityMapping(
    [property: Id(0)] string SourceActivityId,
    [property: Id(1)] string TargetActivityId);

/// <summary>
/// What a migration moves an instance to: a version of the same process, an explicit mapping for
/// renamed elements, and whether event triggers whose definition changed are re-armed.
/// </summary>
/// <param name="UpdateEventTriggers">
/// <c>false</c> (default) keeps every mapped timer, message and signal wait exactly as it is — a
/// timer keeps its due time even when the target's definition differs. <c>true</c> re-arms the
/// waits whose definition changed from the target definition (Camunda 7's <c>updateEventTrigger</c>).
/// </param>
[GenerateSerializer]
public record MigrationPlan(
    [property: Id(0)] string TargetProcessDefinitionId,
    [property: Id(1)] List<ActivityMapping> Mappings,
    [property: Id(2)] bool UpdateEventTriggers = false)
{
    /// <summary>The element <paramref name="sourceActivityId"/> maps to: its explicit pair, else itself.</summary>
    public string MapActivityId(string sourceActivityId)
    {
        foreach (var mapping in Mappings)
        {
            if (mapping.SourceActivityId == sourceActivityId)
                return mapping.TargetActivityId;
        }
        return sourceActivityId;
    }

    /// <summary>The explicit pairs that change an id — what <c>InstanceMigrated</c> records.</summary>
    public List<ActivityMapping> RenamingMappings()
        => Mappings.Where(m => m.SourceActivityId != m.TargetActivityId).Distinct().ToList();
}

/// <summary>
/// Why an instance cannot move. Carries codes and element ids only — never variable values.
/// </summary>
[GenerateSerializer]
public record MigrationRejection(
    [property: Id(0)] string Code,
    [property: Id(1)] string? ActivityId,
    [property: Id(2)] string Message);

/// <summary>The rejection codes of <see cref="InstanceMigrationValidator"/>.</summary>
public static class MigrationRejectionCodes
{
    public const string NotRunning = "not-running";
    public const string SameVersion = "same-version";
    public const string OtherProcess = "other-process";
    public const string InvalidMapping = "invalid-mapping";
    public const string UnmappedActivity = "unmapped-activity";
    public const string TypeChanged = "type-changed";
    public const string ScopeChanged = "scope-changed";
    public const string MultiInstanceChanged = "multi-instance-changed";
    public const string JoinChanged = "join-changed";
    public const string InFlightWork = "in-flight-work";
    public const string CompensationRunning = "compensation-running";
    public const string CallActivityChanged = "call-activity-changed";
    public const string ExternalTaskTypeChanged = "external-task-type-changed";
    public const string EventSubProcessChanged = "event-subprocess-changed";
}

/// <summary>A migration the validator rejected; nothing was changed.</summary>
public sealed class InstanceMigrationRejectedException(IReadOnlyList<MigrationRejection> rejections)
    : InvalidOperationException(
        "Instance migration rejected: " + string.Join(", ", rejections.Select(r => r.Code).Distinct()))
{
    public IReadOnlyList<MigrationRejection> Rejections { get; } = rejections;
}

/// <summary>
/// One migration the instance went through, kept in its state: the UI shows it, the grain uses it
/// to make a repeated migration a no-op, and callbacks addressed to a renamed element are
/// translated through <see cref="Mappings"/>.
/// </summary>
[GenerateSerializer]
public record MigrationRecord(
    [property: Id(0)] string MigrationId,
    [property: Id(1)] string SourceProcessDefinitionId,
    [property: Id(2)] string TargetProcessDefinitionId,
    [property: Id(3)] List<ActivityMapping> Mappings,
    [property: Id(4)] DateTimeOffset MigratedAt);
