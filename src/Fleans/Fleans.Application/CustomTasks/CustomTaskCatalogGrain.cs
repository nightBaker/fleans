using Fleans.Application.Placement;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Application.CustomTasks;

/// <summary>
/// In-memory catalog of custom-task plugin registrations announced by Worker silos.
/// Membership-reconciliation timer prunes entries whose silo has left the cluster.
///
/// State is intentionally ephemeral in this v1: on Core silo restart the catalog is
/// rebuilt as Worker silos restart and re-register. EF-backed persistence is a v2
/// follow-up — design v12 originally specified <c>IPersistentState</c>, deferred here
/// because the per-entry table + migrations dwarf the rest of sub-issue A and the
/// in-memory model is sufficient for the dev/aspire path where Core and Worker silos
/// share a process.
/// </summary>
[CorePlacement]
public sealed partial class CustomTaskCatalogGrain : Grain, ICustomTaskCatalogGrain
{
    private readonly Dictionary<(string TaskType, string SiloName), CustomTaskRegistration> _entries = new();
    private readonly IGrainFactory _grainFactory;
    private readonly ILogger<CustomTaskCatalogGrain> _logger;

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconcileFirstDelay = TimeSpan.FromSeconds(30);

    private static readonly HashSet<SiloStatus> KeepStatuses =
    [
        SiloStatus.Joining,
        SiloStatus.Active,
        SiloStatus.ShuttingDown,
    ];

    public CustomTaskCatalogGrain(IGrainFactory grainFactory, ILogger<CustomTaskCatalogGrain> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
    }

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        this.RegisterGrainTimer(_ => ReconcileWithMembership(),
            new GrainTimerCreationOptions
            {
                DueTime = ReconcileFirstDelay,
                Period = ReconcileInterval,
            });
        return base.OnActivateAsync(cancellationToken);
    }

    public Task Register(CustomTaskRegistration entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = (entry.TaskType, entry.SiloName);
        _entries[key] = entry;
        LogRegistered(entry.TaskType, entry.SiloName);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CustomTaskCatalogEntry>> GetAll()
    {
        var aggregated = _entries.Values
            .GroupBy(e => e.TaskType, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new CustomTaskCatalogEntry(
                    first.TaskType,
                    first.DisplayName,
                    first.ParameterSchema,
                    g.Select(e => e.SiloName).Distinct(StringComparer.Ordinal).ToList());
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<CustomTaskCatalogEntry>>(aggregated);
    }

    public Task<CustomTaskCatalogEntry?> Get(string taskType)
    {
        ArgumentNullException.ThrowIfNull(taskType);
        var matches = _entries.Values
            .Where(e => string.Equals(e.TaskType, taskType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            return Task.FromResult<CustomTaskCatalogEntry?>(null);

        var first = matches[0];
        var entry = new CustomTaskCatalogEntry(
            first.TaskType,
            first.DisplayName,
            first.ParameterSchema,
            matches.Select(e => e.SiloName).Distinct(StringComparer.Ordinal).ToList());
        return Task.FromResult<CustomTaskCatalogEntry?>(entry);
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

            var stale = _entries.Keys
                .Where(k => !aliveSilos.Contains(k.SiloName))
                .ToList();
            foreach (var key in stale)
                _entries.Remove(key);

            if (stale.Count > 0)
                LogReconciled(stale.Count);
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
        Message = "Reconciled custom-task catalog: removed {Count} entries for silos no longer in cluster")]
    private partial void LogReconciled(int count);

    [LoggerMessage(EventId = 9322, Level = LogLevel.Warning,
        Message = "Custom-task catalog reconcile failed; will retry next tick")]
    private partial void LogReconcileFailed(Exception ex);
}
