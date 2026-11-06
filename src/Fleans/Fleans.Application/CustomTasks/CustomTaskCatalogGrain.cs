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
/// Catalog of the custom-task types announced by connected gRPC workers.
/// State is persisted via <see cref="IPersistentState{T}"/> backed by an EF Core
/// grain-storage provider (sub-issue A2 of #357 / PR #434), so the catalog survives
/// Core silo restart.
///
/// A row is kept by an in-memory lease (<see cref="RegisterWorker"/>) the engine renews every
/// heartbeat; the reconcile timer drops it once the lease is older than
/// <see cref="ExternalTaskOptions.WorkerTimeout"/>. A reactivated catalog has no leases, so the
/// first reconcile waits one <see cref="ExternalTaskOptions.WorkerTimeout"/>: connected workers
/// have renewed by then and only the rows of departed ones go.
/// </summary>
[CorePlacement]
public sealed partial class CustomTaskCatalogGrain : Grain, ICustomTaskCatalogGrain
{
    private readonly IPersistentState<CustomTaskCatalogState> _state;
    private readonly ILogger<CustomTaskCatalogGrain> _logger;
    private readonly ExternalTaskOptions _externalTasks;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<(string TaskType, string WorkerName), DateTimeOffset> _workerLeases = [];

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    public CustomTaskCatalogGrain(
        [PersistentState("state", GrainStorageNames.CustomTaskCatalog)] IPersistentState<CustomTaskCatalogState> state,
        ILogger<CustomTaskCatalogGrain> logger,
        IOptions<ExternalTaskOptions> externalTasks,
        TimeProvider timeProvider)
    {
        _state = state;
        _logger = logger;
        _externalTasks = externalTasks.Value;
        _timeProvider = timeProvider;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        this.RegisterGrainTimer(_ => ReconcileLeases(),
            new GrainTimerCreationOptions
            {
                DueTime = _externalTasks.WorkerTimeout,
                Period = ReconcileInterval,
            });
        return base.OnActivateAsync(cancellationToken);
    }

    public async Task RegisterWorker(CustomTaskRegistration entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _workerLeases[LeaseKey(entry.TaskType, entry.SiloName)] = _timeProvider.GetUtcNow();

        var privateBefore = PrivateTaskTypes();
        if (Upsert(entry))
        {
            await _state.WriteStateAsync();
            LogWorkerRegistered(entry.TaskType, entry.SiloName);
            LogAccessChanges(privateBefore);
        }
    }

    private bool Upsert(CustomTaskRegistration entry)
    {
        var schemaJson = entry.ParameterSchema is null
            ? null
            : JsonSerializer.Serialize(entry.ParameterSchema);
        return _state.State.Upsert(entry.TaskType, entry.SiloName, entry.DisplayName, schemaJson,
            CustomTaskAccessRules.NormalizeCategory(entry.Category),
            entry.Visibility == CustomTaskVisibility.Private,
            CustomTaskAccessRules.NormalizeProcessKeys(entry.AllowedProcessKeys));
    }

    public async Task UnregisterWorker(string taskType, string workerName)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        ArgumentNullException.ThrowIfNull(workerName);

        _workerLeases.Remove(LeaseKey(taskType, workerName));
        var privateBefore = PrivateTaskTypes();
        var removed = _state.State.RemoveWhere(e =>
            string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.SiloName, workerName, StringComparison.Ordinal));
        if (removed > 0)
        {
            await _state.WriteStateAsync();
            LogWorkerUnregistered(taskType, workerName);
            LogAccessChanges(privateBefore);
        }
    }

    private static (string, string) LeaseKey(string taskType, string workerName) =>
        (taskType.ToLowerInvariant(), workerName);

    public Task<IReadOnlyList<CustomTaskCatalogEntry>> GetAll()
    {
        var aggregated = _state.State.Entries.Select(e => e.TaskType)
            .Concat(_state.State.PrivateAccess.Select(a => a.TaskType))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(taskType => ToEntry(taskType)!)
            .ToList();
        return Task.FromResult<IReadOnlyList<CustomTaskCatalogEntry>>(aggregated);
    }

    public Task<CustomTaskCatalogEntry?> Get(string taskType)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        return Task.FromResult(ToEntry(taskType));
    }

    public async Task<CustomTaskCatalogEntry> SetAllowedProcesses(string taskType, List<string> processKeys)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        var normalized = CustomTaskAccessRules.NormalizeProcessKeys(processKeys);
        if (!_state.State.SetAllowedProcesses(taskType, normalized))
            throw new InvalidOperationException($"Custom task '{taskType}' is not private, so it has no allow-list.");

        await _state.WriteStateAsync();
        LogAllowedProcessesSet(taskType, normalized.Count);
        return ToEntry(taskType)!;
    }

    public Task<List<string>> GetDisallowedTaskTypes(string processKey, List<string> taskTypes)
    {
        ArgumentNullException.ThrowIfNull(processKey);
        ArgumentNullException.ThrowIfNull(taskTypes);
        return Task.FromResult(taskTypes.Where(t => !_state.State.IsAllowed(t, processKey)).ToList());
    }

    private CustomTaskCatalogEntry? ToEntry(string taskType)
    {
        var rows = _state.State.Entries
            .Where(e => string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var access = _state.State.FindAccess(taskType);
        if (rows.Count == 0 && access is null)
            return null;

        var first = rows.FirstOrDefault();
        return new CustomTaskCatalogEntry(
            first?.TaskType ?? access!.TaskType,
            first?.DisplayName,
            first is null ? null : DeserializeSchema(first.TaskType, first.ParameterSchemaJson),
            rows.Select(e => e.SiloName).Distinct(StringComparer.Ordinal).ToList(),
            first?.Category,
            access is null ? CustomTaskVisibility.Public : CustomTaskVisibility.Private,
            access?.AllowedProcessKeys.ToList());
    }

    private HashSet<string> PrivateTaskTypes() =>
        _state.State.PrivateAccess.Select(a => a.TaskType).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void LogAccessChanges(HashSet<string> privateBefore)
    {
        var privateNow = PrivateTaskTypes();
        foreach (var taskType in privateNow.Except(privateBefore, StringComparer.OrdinalIgnoreCase))
            LogAccessChanged(taskType, CustomTaskVisibility.Private);
        foreach (var taskType in privateBefore.Except(privateNow, StringComparer.OrdinalIgnoreCase))
            LogAccessChanged(taskType, CustomTaskVisibility.Public);
    }

    /// <summary>
    /// Skip-and-warn: malformed schema JSON returns <c>null</c> (UI shows the task type
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

    private async Task ReconcileLeases()
    {
        try
        {
            var leaseCutoff = _timeProvider.GetUtcNow() - _externalTasks.WorkerTimeout;
            foreach (var expired in _workerLeases.Where(kv => kv.Value < leaseCutoff).Select(kv => kv.Key).ToList())
                _workerLeases.Remove(expired);

            var privateBefore = PrivateTaskTypes();
            var removed = _state.State.RemoveWhere(e => !_workerLeases.ContainsKey(LeaseKey(e.TaskType, e.SiloName)));
            if (removed > 0)
            {
                await _state.WriteStateAsync();
                LogReconciled(removed);
                LogAccessChanges(privateBefore);
            }
        }
        catch (Exception ex)
        {
            LogReconcileFailed(ex);
        }
    }

    [LoggerMessage(EventId = 9321, Level = LogLevel.Information,
        Message = "Reconciled custom-task catalog: removed {Count} entries of workers whose lease expired")]
    private partial void LogReconciled(int count);

    [LoggerMessage(EventId = 9322, Level = LogLevel.Warning,
        Message = "Custom-task catalog reconcile failed; will retry next tick")]
    private partial void LogReconcileFailed(Exception ex);

    [LoggerMessage(EventId = 9326, Level = LogLevel.Information,
        Message = "Registered external-task type '{TaskType}' for gRPC worker '{WorkerName}'")]
    private partial void LogWorkerRegistered(string taskType, string workerName);

    [LoggerMessage(EventId = 9327, Level = LogLevel.Information,
        Message = "Unregistered external-task type '{TaskType}' for disconnected gRPC worker '{WorkerName}'")]
    private partial void LogWorkerUnregistered(string taskType, string workerName);

    [LoggerMessage(EventId = 9328, Level = LogLevel.Information,
        Message = "Custom task '{TaskType}' is now {Visibility}")]
    private partial void LogAccessChanged(string taskType, CustomTaskVisibility visibility);

    [LoggerMessage(EventId = 9329, Level = LogLevel.Information,
        Message = "Allow-list of private custom task '{TaskType}' set to {Count} process(es)")]
    private partial void LogAllowedProcessesSet(string taskType, int count);

    [LoggerMessage(EventId = 9325, Level = LogLevel.Warning,
        Message = "Custom-task catalog: failed to deserialize ParameterSchemaJson for taskType='{TaskType}'; returning null schema (UI will hide parameter widgets for this task type)")]
    private partial void LogSchemaDeserializeFailed(Exception ex, string taskType);
}
