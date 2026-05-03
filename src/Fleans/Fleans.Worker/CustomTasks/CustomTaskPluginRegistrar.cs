using Fleans.Application.CustomTasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.Worker.CustomTasks;

/// <summary>
/// Pushes Worker-side plugin registrations to the Core-side <see cref="ICustomTaskCatalogGrain"/>
/// after the silo has fully joined the cluster (<see cref="ServiceLifecycleStage.Active"/>).
/// One-shot at startup with bounded retry; departure is detected by the catalog's own
/// membership-reconciliation timer rather than by silo shutdown signals.
/// </summary>
internal sealed partial class CustomTaskPluginRegistrar : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly IGrainFactory _grainFactory;
    private readonly ILocalSiloDetails _siloDetails;
    private readonly IEnumerable<CustomTaskPluginDescriptor> _descriptors;
    private readonly ILogger<CustomTaskPluginRegistrar> _logger;

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    ];

    public CustomTaskPluginRegistrar(
        IGrainFactory grainFactory,
        ILocalSiloDetails siloDetails,
        IEnumerable<CustomTaskPluginDescriptor> descriptors,
        ILogger<CustomTaskPluginRegistrar> logger)
    {
        _grainFactory = grainFactory;
        _siloDetails = siloDetails;
        _descriptors = descriptors;
        _logger = logger;
    }

    public void Participate(ISiloLifecycle observer) =>
        observer.Subscribe(
            nameof(CustomTaskPluginRegistrar),
            ServiceLifecycleStage.Active,
            OnStart);

    private async Task OnStart(CancellationToken ct)
    {
        var catalog = _grainFactory.GetGrain<ICustomTaskCatalogGrain>(0);
        foreach (var descriptor in _descriptors)
            await RegisterWithRetry(catalog, descriptor, ct);
    }

    private async Task RegisterWithRetry(
        ICustomTaskCatalogGrain catalog,
        CustomTaskPluginDescriptor descriptor,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                await catalog.Register(new CustomTaskRegistration(
                    descriptor.TaskType,
                    descriptor.DisplayName,
                    descriptor.ParameterSchema,
                    _siloDetails.Name));
                LogRegistered(descriptor.TaskType, _siloDetails.Name);
                return;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                LogRetrying(ex, descriptor.TaskType, attempt + 1, RetryDelays.Length + 1, RetryDelays[attempt]);
                try { await Task.Delay(RetryDelays[attempt], ct); }
                catch (OperationCanceledException) { return; }
            }
            catch (Exception ex)
            {
                LogPermanentlyFailed(ex, descriptor.TaskType, _siloDetails.Name);
                return; // warn-and-continue — handler still receives events; only catalog UI is blind
            }
        }
    }

    [LoggerMessage(EventId = 9330, Level = LogLevel.Information,
        Message = "Registered custom-task plugin '{TaskType}' on silo '{SiloName}' with the catalog")]
    private partial void LogRegistered(string taskType, string siloName);

    [LoggerMessage(EventId = 9331, Level = LogLevel.Warning,
        Message = "Catalog Register failed for '{TaskType}' (attempt {Attempt}/{Max}); retrying in {Delay}")]
    private partial void LogRetrying(Exception ex, string taskType, int attempt, int max, TimeSpan delay);

    [LoggerMessage(EventId = 9332, Level = LogLevel.Error,
        Message = "Catalog Register permanently failed for '{TaskType}' on silo '{SiloName}' — plugin will be invisible to the catalog UI until next silo restart")]
    private partial void LogPermanentlyFailed(Exception ex, string taskType, string siloName);
}
