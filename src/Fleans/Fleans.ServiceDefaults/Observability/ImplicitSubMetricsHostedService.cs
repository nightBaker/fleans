using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using StackExchange.Redis;

namespace Fleans.ServiceDefaults.Observability;

/// <summary>
/// Periodically logs (a) active grain count per handler grain type and (b) Redis
/// <c>PubSubStore</c> key count. Surfaces the per-instance stream-sharding cost (#565)
/// so operators can observe the activation explosion and PubSubStore growth before
/// committing to the #591 cleanup work (see <c>docs/plans/2026-05-19-…</c>).
///
/// Polls every <see cref="PollInterval"/> (default 30s) and emits two structured log
/// lines: <c>LogActiveImplicitSubGrainCount</c> (EventId 7000) and
/// <c>LogPubSubStoreKeyCount</c> (EventId 7001). Both at <c>Information</c> level.
/// </summary>
public sealed partial class ImplicitSubMetricsHostedService : BackgroundService
{
    /// <summary>Polling interval. Trade-off: shorter = more accurate timeseries; longer = less log noise.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<ImplicitSubMetricsHostedService> _logger;
    private readonly IClusterClient _clusterClient;
    private readonly IConnectionMultiplexer? _redis;

    public ImplicitSubMetricsHostedService(
        ILogger<ImplicitSubMetricsHostedService> logger,
        IClusterClient clusterClient,
        IConnectionMultiplexer? redis = null)
    {
        _logger = logger;
        _clusterClient = clusterClient;
        _redis = redis;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the silo a moment to fully start before the first probe — IManagementGrain
        // is not reliably callable during the first second of silo lifetime.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CaptureGrainStatistics(stoppingToken);
                await CaptureRedisPubSubStoreKeyCount(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPollFailure(ex);
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task CaptureGrainStatistics(CancellationToken ct)
    {
        var management = _clusterClient.GetGrain<IManagementGrain>(0);
        var stats = await management.GetSimpleGrainStatistics();

        // We only care about handler grain types — the implicit-subscription targets that
        // explode per-WorkflowInstanceId. Filter by namespace/typename heuristic to keep the
        // log digestible. Handler grains live under Fleans.Application or Fleans.Worker.
        var handlerStats = stats
            .Where(s => s.GrainType.Contains("EventHandler", StringComparison.Ordinal)
                     || s.GrainType.Contains("CustomTaskHandler", StringComparison.Ordinal)
                     || s.GrainType.Contains("Handler", StringComparison.Ordinal))
            .OrderBy(s => s.GrainType, StringComparer.Ordinal);

        var totalHandlerActivations = handlerStats.Sum(s => s.ActivationCount);
        var perTypeBreakdown = string.Join(", ", handlerStats
            .Where(s => s.ActivationCount > 0)
            .Select(s => $"{ShortTypeName(s.GrainType)}={s.ActivationCount}"));

        LogActiveImplicitSubGrainCount(totalHandlerActivations, perTypeBreakdown);
    }

    private async Task CaptureRedisPubSubStoreKeyCount(CancellationToken ct)
    {
        if (_redis is null)
        {
            // Memory PubSubStore (tests, Aspire dev) — no key-count probe path available
            // without crossing IGrainStorage boundary. Skip silently rather than log noise.
            return;
        }

        var server = _redis.GetServers().FirstOrDefault(s => s.IsConnected);
        if (server is null) return;

        // SCAN MATCH "PubSubStore/*" — Orleans Redis grain storage key shape. Counting
        // via SCAN avoids blocking the Redis server on KEYS for large datasets.
        long keyCount = 0;
        await foreach (var _ in server.KeysAsync(pattern: "PubSubStore/*", pageSize: 1000)
            .WithCancellation(ct))
        {
            keyCount++;
        }

        LogPubSubStoreKeyCount(keyCount);
    }

    private static string ShortTypeName(string fullName)
    {
        // Trim namespace prefix for log readability. "Fleans.Application.Events.Handlers.WorkflowExecuteScriptEventHandler"
        // → "WorkflowExecuteScriptEventHandler".
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 && lastDot < fullName.Length - 1 ? fullName[(lastDot + 1)..] : fullName;
    }

    [LoggerMessage(EventId = 7000, Level = LogLevel.Information,
        Message = "Implicit-subscription handler-grain activation count: {TotalActivations} (breakdown: {PerTypeBreakdown})")]
    private partial void LogActiveImplicitSubGrainCount(int totalActivations, string perTypeBreakdown);

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information,
        Message = "Orleans PubSubStore Redis key count: {KeyCount}")]
    private partial void LogPubSubStoreKeyCount(long keyCount);

    [LoggerMessage(EventId = 7099, Level = LogLevel.Warning,
        Message = "ImplicitSubMetricsHostedService poll iteration failed; will retry on next interval")]
    private partial void LogPollFailure(Exception ex);
}
