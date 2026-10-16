using System.Reflection;
using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Orleans.Serialization;

namespace Fleans.Persistence.Events;

/// <summary>
/// The WorkflowInstance journal (WorkflowEvents, JSON) and its snapshots (WorkflowSnapshots, the full
/// grain state in Orleans binary form). Used by the grain's ICustomStorageInterface implementation.
/// The read model is a separate projection (<see cref="IWorkflowStateProjection"/>).
/// </summary>
public class EfCoreEventStore : IEventStore
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;
    private readonly Serializer _serializer;
    private readonly int _maxEventsPerLoad;

    internal static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.Auto,
        PreserveReferencesHandling = PreserveReferencesHandling.Objects,
        SerializationBinder = new EventStoreSerializationBinder(),
        ContractResolver = new PrivateSetterContractResolver(),
        NullValueHandling = NullValueHandling.Include,
        MissingMemberHandling = MissingMemberHandling.Ignore
    };

    public EfCoreEventStore(
        IDbContextFactory<FleanCommandDbContext> dbContextFactory,
        Serializer serializer,
        IOptions<FleansPersistenceOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _serializer = serializer;
        _maxEventsPerLoad = options.Value.MaxEventsPerLoad;
        SnapshotInterval = options.Value.SnapshotInterval;
    }

    public int SnapshotInterval { get; }

    /// <summary>
    /// The latest snapshot and the version it covers, or <c>(null, 0)</c> when there is none.
    /// Throws when the payload cannot be deserialized (e.g. the state shape changed).
    /// </summary>
    public async Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(string grainId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var snapshot = await db.WorkflowSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.GrainId == grainId);
        if (snapshot is null)
            return (null, 0);

        return (_serializer.Deserialize<WorkflowInstanceState>(snapshot.Payload), snapshot.Version);
    }

    /// <summary>
    /// Loads all events for a grain after the given version, ordered by version.
    /// </summary>
    public async Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(
        string grainId, int afterVersion)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        // Take one extra row so we can detect "would exceed cap" without reading all rows.
        var limit = _maxEventsPerLoad < int.MaxValue ? _maxEventsPerLoad + 1 : int.MaxValue;
        var eventEntities = await db.WorkflowEvents
            .AsNoTracking()
            .Where(e => e.GrainId == grainId && e.Version > afterVersion)
            .OrderBy(e => e.Version)
            .Take(limit)
            .ToListAsync();

        if (eventEntities.Count > _maxEventsPerLoad)
            throw new InvalidOperationException(
                $"Refusing to load more than {_maxEventsPerLoad} events for grain '{grainId}' " +
                $"(afterVersion={afterVersion}). This indicates snapshotting is broken — " +
                $"investigate WorkflowInstance.ApplyUpdatesToStorage / WriteSnapshotAsync. " +
                $"Increase Persistence:MaxEventsPerLoad if this is intentional.");

        return eventEntities
            .Select(e => EventTypeRegistry.Deserialize(e.EventType, e.Payload, JsonSettings))
            .ToList();
    }

    /// <summary>
    /// Appends events as versions <c>expectedVersion + 1</c>, … (1-based; the store does the
    /// numbering so callers pass the grain's event count). Returns false if a version conflict
    /// occurs (unique constraint violation).
    /// </summary>
    public async Task<bool> AppendEventsAsync(
        string grainId,
        IReadOnlyList<IDomainEvent> events,
        int expectedVersion)
    {
        if (events.Count == 0) return true;

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        for (int i = 0; i < events.Count; i++)
        {
            db.WorkflowEvents.Add(new WorkflowEventEntity
            {
                GrainId = grainId,
                Version = expectedVersion + 1 + i,
                EventType = EventTypeRegistry.GetEventType(events[i]),
                Payload = JsonConvert.SerializeObject(events[i], JsonSettings),
                Timestamp = events[i] is WorkflowEvent we ? we.OccurredAt : DateTimeOffset.UtcNow
            });
        }

        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// Upserts the grain's single snapshot row — an UPDATE first, so the previous payload is never
    /// read back; an INSERT only for the grain's first snapshot.
    /// </summary>
    public async Task WriteSnapshotAsync(string grainId, int version, WorkflowInstanceState state)
    {
        var payload = _serializer.SerializeToArray(state);
        var now = DateTimeOffset.UtcNow;

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var updated = await db.WorkflowSnapshots
            .Where(s => s.GrainId == grainId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Version, version)
                .SetProperty(x => x.Payload, payload)
                .SetProperty(x => x.Timestamp, now));
        if (updated > 0)
            return;

        db.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
        {
            GrainId = grainId,
            Version = version,
            Payload = payload,
            Timestamp = now
        });
        await db.SaveChangesAsync();
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        // SQLite unique constraint violation
        if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            return true;
        // SQL Server unique constraint violation
        if (message.Contains("Cannot insert duplicate key", StringComparison.OrdinalIgnoreCase))
            return true;
        // PostgreSQL unique constraint violation
        if (message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }
}

/// <summary>
/// Serialization binder for the event store. Allows types from
/// <see cref="Fleans.Domain"/> and the BCL assemblies enumerated in
/// <see cref="JsonAssemblyAllowList"/>. Anything else — including BCL
/// assemblies that ship classic Newtonsoft gadget chains
/// (<c>System.Diagnostics.Process</c>, <c>System.Configuration.Install.AssemblyInstaller</c>,
/// etc.) — is rejected with <see cref="JsonSerializationException"/>.
/// </summary>
internal sealed class EventStoreSerializationBinder : DefaultSerializationBinder
{
    private static readonly Assembly DomainAssembly = typeof(WorkflowDefinition).Assembly;

    public override Type BindToType(string? assemblyName, string typeName)
    {
        var type = base.BindToType(assemblyName, typeName);
        if (type.Assembly != DomainAssembly && !JsonAssemblyAllowList.IsAllowedBclAssembly(type.Assembly))
            throw new JsonSerializationException(
                $"Deserialization of type '{type.FullName}' from assembly '{type.Assembly.FullName}' is not allowed. " +
                $"Only types from '{DomainAssembly.GetName().Name}' and a curated BCL allowlist are permitted.");
        return type;
    }
}

/// <summary>
/// Contract resolver that allows deserialization of properties with private setters.
/// Required for WorkflowInstanceState snapshot deserialization (e.g. Id has private set).
/// </summary>
internal sealed class PrivateSetterContractResolver : DefaultContractResolver
{
    protected override JsonProperty CreateProperty(
        System.Reflection.MemberInfo member,
        MemberSerialization memberSerialization)
    {
        var prop = base.CreateProperty(member, memberSerialization);
        if (!prop.Writable && member is System.Reflection.PropertyInfo propertyInfo)
        {
            prop.Writable = propertyInfo.GetSetMethod(nonPublic: true) is not null;
        }
        return prop;
    }
}
