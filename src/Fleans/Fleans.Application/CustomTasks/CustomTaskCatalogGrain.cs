using System.Text.Json;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Placement;
using Fleans.Domain;
using Fleans.Domain.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// Pure logic for the catalog reconcile sanity guard. Extracted so unit tests
/// can target the guard's decision directly without standing up a TestCluster
/// or mocking <see cref="IManagementGrain"/>. See #659.
/// </summary>
internal static class ReconcileGuard
{
    /// <summary>
    /// Detects the "membership directory anomaly" case where the catalog has
    /// entries but the management grain reports zero alive silos — almost
    /// certainly a transient Redis/membership-table blip, not a real "every
    /// silo left the cluster" event. The reconciler must skip writes in this
    /// case to avoid silently wiping the persisted catalog.
    /// </summary>
    public static bool IsAnomalousEmptyAliveSilos(int aliveSilosCount, int currentEntryCount)
        => aliveSilosCount == 0 && currentEntryCount > 0;
}

/// <summary>
/// Catalog of custom-task plugin registrations announced by Worker silos.
/// State is persisted via <see cref="IPersistentState{T}"/> backed by an EF Core
/// grain-storage provider (sub-issue A2 of #357 / PR #434), so the catalog survives
/// Core silo restart. The membership-reconciliation timer prunes entries whose silo
/// has left the cluster.
///
/// External gRPC workers (<see cref="RegisterWorker"/>) are not silos: their rows are kept by an
/// in-memory lease the engine renews every heartbeat, and the reconcile drops them once the lease
/// is older than <see cref="ExternalTaskOptions.WorkerTimeout"/>. A reactivated catalog has no
/// leases, so its first reconcile drops worker rows; the next heartbeat (at most
/// <see cref="ExternalTaskOptions.HeartbeatInterval"/> later) restores them.
/// </summary>
[CorePlacement]
public sealed partial class CustomTaskCatalogGrain : Grain, ICustomTaskCatalogGrain
{
    private readonly IPersistentState<CustomTaskCatalogState> _state;
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<CustomTaskCatalogGrain> _logger;
    private readonly ExternalTaskOptions _externalTasks;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(string TaskType, string WorkerName), DateTimeOffset> _workerLeases = [];

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconcileFirstDelay = TimeSpan.FromSeconds(30);

    private static readonly HashSet<SiloStatus> KeepStatuses =
    [
        SiloStatus.Joining,
        SiloStatus.Active,
        SiloStatus.ShuttingDown,
    ];

    public CustomTaskCatalogGrain(
        [PersistentState("state", GrainStorageNames.CustomTaskCatalog)] IPersistentState<CustomTaskCatalogState> state,
        IGrainFactory grainFactory,
        ILogger<CustomTaskCatalogGrain> logger,
        IOptions<ExternalTaskOptions> externalTasks,
        TimeProvider timeProvider)
    {
        _state = state;
        _grainFactory = grainFactory;
        _logger = logger;
        _externalTasks = externalTasks.Value;
        _timeProvider = timeProvider;
    }

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // Reconcile persisted state against current cluster membership immediately so
        // entries from silos that left while Core was down are dropped before the
        // first request lands. Subsequent ticks happen on the timer below.
        await ReconcileWithMembership();
        this.RegisterGrainTimer(_ => ReconcileWithMembership(),
            new GrainTimerCreationOptions
            {
                DueTime = ReconcileFirstDelay,
                Period = ReconcileInterval,
            });
        await base.OnActivateAsync(cancellationToken);
    }

    public async Task Register(CustomTaskRegistration entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var schemaJson = entry.ParameterSchema is null
            ? null
            : JsonSerializer.Serialize(entry.ParameterSchema);

        var changed = _state.State.Upsert(entry.TaskType, entry.SiloName, entry.DisplayName, schemaJson);
        if (changed)
        {
            await _state.WriteStateAsync();
            LogRegistered(entry.TaskType, entry.SiloName);
        }
    }

    public async Task RegisterWorker(CustomTaskRegistration entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _workerLeases[LeaseKey(entry.TaskType, entry.SiloName)] = _timeProvider.GetUtcNow();

        var schemaJson = entry.ParameterSchema is null
            ? null
            : JsonSerializer.Serialize(entry.ParameterSchema);

        if (_state.State.Upsert(entry.TaskType, entry.SiloName, entry.DisplayName, schemaJson))
        {
            await _state.WriteStateAsync();
            LogWorkerRegistered(entry.TaskType, entry.SiloName);
        }
    }

    public async Task UnregisterWorker(string taskType, string workerName)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        ArgumentNullException.ThrowIfNull(workerName);

        _workerLeases.Remove(LeaseKey(taskType, workerName));
        var removed = _state.State.RemoveWhere(e =>
            string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.SiloName, workerName, StringComparison.Ordinal));
        if (removed > 0)
        {
            await _state.WriteStateAsync();
            LogWorkerUnregistered(taskType, workerName);
        }
    }

    private static (string, string) LeaseKey(string taskType, string workerName) =>
        (taskType.ToLowerInvariant(), workerName);

    public Task<IReadOnlyList<CustomTaskCatalogEntry>> GetAll()
    {
        var aggregated = _state.State.Entries
            .GroupBy(e => e.TaskType, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new CustomTaskCatalogEntry(
                    first.TaskType,
                    first.DisplayName,
                    DeserializeSchema(first.TaskType, first.ParameterSchemaJson),
                    g.Select(e => e.SiloName).Distinct(StringComparer.Ordinal).ToList());
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<CustomTaskCatalogEntry>>(aggregated);
    }

    public Task<CustomTaskCatalogEntry?> Get(string taskType)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        var matches = _state.State.Entries
            .Where(e => string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return Task.FromResult<CustomTaskCatalogEntry?>(null);

        var first = matches[0];
        var entry = new CustomTaskCatalogEntry(
            first.TaskType,
            first.DisplayName,
            DeserializeSchema(first.TaskType, first.ParameterSchemaJson),
            matches.Select(e => e.SiloName).Distinct(StringComparer.Ordinal).ToList());
        return Task.FromResult<CustomTaskCatalogEntry?>(entry);
    }

    /// <summary>
    /// Skip-and-warn: malformed schema JSON returns <c>null</c> (UI shows the plugin
    /// without parameter widgets) rather than throwing and breaking the entire catalog.
    /// </summary>
    private CustomTaskParameterSchema? DeserializeSchema(string taskType, string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CustomTaskParameterSchema>(json);
        }
        catch (JsonException ex)
        {
            LogSchemaDeserializeFailed(ex, taskType);
            return null;
        }
    }

    private async Task ReconcileWithMembership()
    {
        try
        {
            var management = _grainFactory.GetGrain<IManagementGrain>(0);
            var hosts = await management.GetDetailedHosts(onlyActive: false);
            var aliveSilos = hosts
                .Where(h => KeepStatuses.Contains(h.Status))
                .Select(h => h.SiloName)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.Ordinal);

            if (ReconcileGuard.IsAnomalousEmptyAliveSilos(aliveSilos.Count, _state.State.Entries.Count))
            {
                LogReconcileSkippedAnomalousEmptyAliveSilos(_state.State.Entries.Count);
                return;
            }

            var leaseCutoff = _timeProvider.GetUtcNow() - _externalTasks.WorkerTimeout;
            foreach (var expired in _workerLeases.Where(kv => kv.Value < leaseCutoff).Select(kv => kv.Key).ToList())
                _workerLeases.Remove(expired);

            var removed = _state.State.RemoveWhere(e =>
                !aliveSilos.Contains(e.SiloName) && !_workerLeases.ContainsKey(LeaseKey(e.TaskType, e.SiloName)));
            if (removed > 0)
            {
                await _state.WriteStateAsync();
                LogReconciled(removed);
            }
        }
        catch (Exception ex)
        {
            LogReconcileFailed(ex);
        }
    }

    [LoggerMessage(EventId = 9320, Level = LogLevel.Information,
        Message = "Registered custom-task plugin '{TaskType}' on silo '{SiloName}'")]
    private partial void LogRegistered(string taskType, string siloName);

    [LoggerMessage(EventId = 9321, Level = LogLevel.Information,
        Message = "Reconciled custom-task catalog: removed {Count} entries for silos no longer in cluster or external workers whose lease expired")]
    private partial void LogReconciled(int count);

    [LoggerMessage(EventId = 9322, Level = LogLevel.Warning,
        Message = "Custom-task catalog reconcile failed; will retry next tick")]
    private partial void LogReconcileFailed(Exception ex);

    [LoggerMessage(EventId = 9323, Level = LogLevel.Warning,
        Message = "Custom-task catalog reconcile observed empty alive-silos list with {EntryCount} catalog entries still present — treating as transient membership anomaly; skipping this reconcile tick.")]
    private partial void LogReconcileSkippedAnomalousEmptyAliveSilos(int entryCount);

    [LoggerMessage(EventId = 9326, Level = LogLevel.Information,
        Message = "Registered external-task type '{TaskType}' for gRPC worker '{WorkerName}'")]
    private partial void LogWorkerRegistered(string taskType, string workerName);

    [LoggerMessage(EventId = 9327, Level = LogLevel.Information,
        Message = "Unregistered external-task type '{TaskType}' for disconnected gRPC worker '{WorkerName}'")]
    private partial void LogWorkerUnregistered(string taskType, string workerName);

    [LoggerMessage(EventId = 9325, Level = LogLevel.Warning,
        Message = "Custom-task catalog: failed to deserialize ParameterSchemaJson for taskType='{TaskType}'; returning null schema (UI will hide parameter widgets for this plugin)")]
    private partial void LogSchemaDeserializeFailed(Exception ex, string taskType);
}
