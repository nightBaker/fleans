using System.Reflection;
using Fleans.Domain;
using Fleans.Domain.Events;
using Fleans.Domain.States;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Fleans.Persistence.Events;

/// <summary>
/// Encapsulates all database operations for event and snapshot persistence.
/// Used by the WorkflowInstance grain's ICustomStorageInterface implementation.
/// Snapshots are stored in normalized SQL tables via IWorkflowStateProjection,
/// with version metadata tracked in the WorkflowSnapshots table.
/// </summary>
public class EfCoreEventStore : IEventStore
{
    private readonly IDbContextFactory<FleanCommandDbContext> _dbContextFactory;
    private readonly IWorkflowStateProjection _stateProjection;

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
        IWorkflowStateProjection stateProjection)
    {
        _dbContextFactory = dbContextFactory;
        _stateProjection = stateProjection;
    }

    /// <summary>
    /// Loads the latest snapshot for a grain, or returns (null, 0) if none exists.
    /// State is read from the normalized WorkflowInstances tables via IWorkflowStateProjection.
    /// Version metadata is read from the WorkflowSnapshots table.
    /// </summary>
    public async Task<(WorkflowInstanceState? State, int Version)> ReadSnapshotAsync(
        string grainId)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var snapshot = await db.WorkflowSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.GrainId == grainId);

        if (snapshot is null)
            return (null, 0);

        var state = await _stateProjection.ReadAsync(grainId);

        return (state, snapshot.Version);
    }

    /// <summary>
    /// Loads all events for a grain after the given version, ordered by version.
    /// </summary>
    public async Task<IReadOnlyList<IDomainEvent>> ReadEventsAsync(
        string grainId, int afterVersion)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var eventEntities = await db.WorkflowEvents
            .AsNoTracking()
            .Where(e => e.GrainId == grainId && e.Version > afterVersion)
            .OrderBy(e => e.Version)
            .ToListAsync();

        return eventEntities
            .Select(e => EventTypeRegistry.Deserialize(e.EventType, e.Payload, JsonSettings))
            .ToList();
    }

    /// <summary>
    /// Appends events starting at the given version.
    /// Returns false if a version conflict occurs (unique constraint violation).
    /// </summary>
    public async Task<bool> AppendEventsAsync(
        string grainId,
        IReadOnlyList<IDomainEvent> events,
        int startVersion)
    {
        if (events.Count == 0) return true;

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        for (int i = 0; i < events.Count; i++)
        {
            db.WorkflowEvents.Add(new WorkflowEventEntity
            {
                GrainId = grainId,
                Version = startVersion + i,
                EventType = EventTypeRegistry.GetEventType(events[i]),
                Payload = JsonConvert.SerializeObject(events[i], JsonSettings),
                Timestamp = DateTimeOffset.UtcNow
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
    /// Upserts a snapshot for the given grain.
    /// State is written to normalized WorkflowInstances tables via IWorkflowStateProjection.
    /// Version metadata is tracked in the WorkflowSnapshots table.
    /// </summary>
    public async Task WriteSnapshotAsync(
        string grainId,
        int version,
        WorkflowInstanceState state)
    {
        await _stateProjection.WriteAsync(grainId, state);

        await using var db = await _dbContextFactory.CreateDbContextAsync();

        var existing = await db.WorkflowSnapshots
            .FirstOrDefaultAsync(s => s.GrainId == grainId);

        if (existing is not null)
        {
            existing.Version = version;
            existing.Timestamp = DateTimeOffset.UtcNow;
        }
        else
        {
            db.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
            {
                GrainId = grainId,
                Version = version,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

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
/// Serialization binder for event store that allows Fleans.Domain types
/// and BCL/system assembly types (needed for ExpandoObject, List&lt;T&gt;, arrays, etc.).
/// </summary>
internal sealed class EventStoreSerializationBinder : DefaultSerializationBinder
{
    private static readonly Assembly DomainAssembly = typeof(WorkflowDefinition).Assembly;

    public override Type BindToType(string? assemblyName, string typeName)
    {
        var type = base.BindToType(assemblyName, typeName);
        if (type.Assembly != DomainAssembly && !IsSystemAssembly(type.Assembly))
            throw new JsonSerializationException(
                $"Deserialization of type '{type.FullName}' from assembly '{type.Assembly.FullName}' is not allowed. " +
                $"Only types from '{DomainAssembly.GetName().Name}' and system assemblies are permitted.");
        return type;
    }

    private static bool IsSystemAssembly(Assembly assembly)
    {
        var name = assembly.GetName().Name;
        if (name is null) return false;
        return name.StartsWith("System", StringComparison.Ordinal)
            || name is "mscorlib" or "netstandard";
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
