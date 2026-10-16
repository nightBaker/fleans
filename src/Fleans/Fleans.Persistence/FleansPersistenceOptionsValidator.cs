using Microsoft.Extensions.Options;

namespace Fleans.Persistence;

/// <summary>
/// Rejects event-store settings that would make workflow instances fail to activate: replay after
/// a snapshot reads up to <see cref="FleansPersistenceOptions.SnapshotInterval"/> events, and
/// <see cref="FleansPersistenceOptions.MaxEventsPerLoad"/> must allow that.
/// </summary>
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
                $"Persistence:SnapshotInterval ({options.SnapshotInterval}); otherwise an instance between " +
                "two snapshots cannot be loaded.");

        return ValidateOptionsResult.Success;
    }
}
