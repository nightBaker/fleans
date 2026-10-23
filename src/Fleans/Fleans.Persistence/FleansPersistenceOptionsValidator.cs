using Microsoft.Extensions.Options;

namespace Fleans.Persistence;

/// <summary>
/// Rejects event-store settings under which even healthy instances cannot activate: between two
/// snapshots the journal tail grows to about <see cref="FleansPersistenceOptions.SnapshotInterval"/>
/// events, so <see cref="FleansPersistenceOptions.MaxEventsPerLoad"/> must exceed it.
/// </summary>
/// <remarks>
/// Passing is not a guarantee. A tail can still outgrow the cap: one very large batch appended
/// just before a crash, or snapshot writes that keep failing (EventId 1131, logged as an error).
/// The cap is a safety valve against loading a runaway journal; raise it for such an instance.
/// </remarks>
public sealed class FleansPersistenceOptionsValidator : IValidateOptions<FleansPersistenceOptions>
{
    public ValidateOptionsResult Validate(string? name, FleansPersistenceOptions options)
    {
        if (options.SnapshotInterval < 1)
            return ValidateOptionsResult.Fail(
                $"Persistence:SnapshotInterval must be at least 1 (was {options.SnapshotInterval}).");

        if (options.MaxEventsPerLoad <= options.SnapshotInterval)
            return ValidateOptionsResult.Fail(
                $"Persistence:MaxEventsPerLoad ({options.MaxEventsPerLoad}) must be greater than " +
                $"Persistence:SnapshotInterval ({options.SnapshotInterval}); otherwise even an instance " +
                "whose snapshots are written on time cannot be loaded between two of them.");

        var shardCount = options.Journal.Citus.ShardCount;
        if (shardCount < 1 || shardCount > CitusJournalOptions.MaxShardCount)
            return ValidateOptionsResult.Fail(
                $"Persistence:Journal:Citus:ShardCount must be between 1 and {CitusJournalOptions.MaxShardCount} (was {shardCount}).");

        return ValidateOptionsResult.Success;
    }
}
