using Fleans.Application.Grains;
using Fleans.Application.Observability;
using Fleans.Application.QueryModels;
using Fleans.Domain.InstanceMigration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.Application.InstanceMigration;

/// <summary>
/// Fans a migration out to the instance grains, with bounded parallelism and a per-request cap
/// (#1069). Each grain validates and migrates itself, so the read model's lag only costs a
/// <c>not-running</c> or <c>same-version</c> rejection, never a wrong move. Synchronous on
/// purpose: an async bulk job waits for a real need (design doc, "Out of scope").
/// </summary>
public sealed partial class InstanceMigrationService : IInstanceMigrationService
{
    private readonly IGrainFactory _grainFactory;
    private readonly IWorkflowQueryService _query;
    private readonly InstanceMigrationOptions _options;
    private readonly ILogger<InstanceMigrationService> _logger;

    public InstanceMigrationService(
        IGrainFactory grainFactory, IWorkflowQueryService query,
        IOptions<InstanceMigrationOptions> options, ILogger<InstanceMigrationService> logger)
    {
        _grainFactory = grainFactory;
        _query = query;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InstanceMigrationBatchResult> MigrateAsync(
        InstanceMigrationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetProcessDefinitionId);
        var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;

        var (instanceIds, continuationToken) = await ResolveInstances(request);

        using var activity = FleansDiagnostics.ActivitySource.StartActivity("fleans.instance.migrate_batch");
        activity?.SetTag("fleans.migration.request_id", requestId);
        activity?.SetTag("fleans.migration.target", request.TargetProcessDefinitionId);
        activity?.SetTag("fleans.migration.dry_run", request.DryRun);
        activity?.SetTag("fleans.migration.instances", instanceIds.Count);
        LogBatchStarted(requestId, instanceIds.Count, request.TargetProcessDefinitionId, request.DryRun);

        var plan = ToPlan(request);
        var results = new InstanceMigrationResult[instanceIds.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, instanceIds.Count),
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism, CancellationToken = cancellationToken },
            async (i, _) => results[i] = await MigrateOne(instanceIds[i], requestId, plan, request.DryRun));

        LogBatchFinished(requestId, results.Length,
            results.Count(r => r.Outcome is InstanceMigrationOutcome.Migrated or InstanceMigrationOutcome.Migratable),
            results.Count(r => r.Outcome == InstanceMigrationOutcome.Rejected),
            results.Count(r => r.Outcome == InstanceMigrationOutcome.Failed));
        return new InstanceMigrationBatchResult(requestId, request.DryRun, results, continuationToken);
    }

    public async Task<InstanceMigrationResult> MigrateInstanceAsync(Guid instanceId, InstanceMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetProcessDefinitionId);
        var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId;
        // Unknown instance or target version → KeyNotFoundException → 404 at the API.
        return await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId)
            .Migrate(MigrationId(requestId, instanceId), ToPlan(request), request.DryRun);
    }

    public async Task<InstanceMigrationPreview?> PreviewAsync(string processDefinitionKey, int fromVersion, int toVersion)
    {
        var sourceId = await _query.GetProcessDefinitionId(processDefinitionKey, fromVersion);
        var targetId = await _query.GetProcessDefinitionId(processDefinitionKey, toVersion);
        if (sourceId is null || targetId is null)
            return null;

        var definitions = _grainFactory.GetGrain<IProcessDefinitionGrain>(processDefinitionKey);
        var source = await definitions.GetDefinitionById(sourceId);
        var target = await definitions.GetDefinitionById(targetId);
        var running = await _query.CountRunningInstances(sourceId);

        return new InstanceMigrationPreview(processDefinitionKey, sourceId, fromVersion, targetId, toVersion,
            running, MigrationPreviewBuilder.Build(source, target),
            MigrationPreviewBuilder.AllElements(target)
                .Select(e => new MigrationTargetElement(e.ActivityId, e.GetType().Name)).ToList());
    }

    private async Task<(IReadOnlyList<Guid> InstanceIds, string? ContinuationToken)> ResolveInstances(InstanceMigrationRequest request)
    {
        var hasIds = request.InstanceIds is { Count: > 0 };
        var hasSource = !string.IsNullOrWhiteSpace(request.SourceProcessDefinitionId);
        if (hasIds == hasSource)
            throw new ArgumentException("Give either instanceIds or sourceProcessDefinitionId.");

        if (hasIds)
        {
            var ids = request.InstanceIds!.Distinct().ToList();
            if (ids.Count > _options.MaxBatchSize)
                throw new ArgumentException(
                    $"{ids.Count} instances is more than {InstanceMigrationOptions.SectionName}:MaxBatchSize ({_options.MaxBatchSize}); split the request.");
            return (ids, null);
        }

        Guid? after = null;
        if (!string.IsNullOrWhiteSpace(request.ContinuationToken))
        {
            if (!Guid.TryParse(request.ContinuationToken, out var token))
                throw new ArgumentException("The continuation token is not valid.");
            after = token;
        }

        // One more than the cap tells whether another page follows.
        var page = await _query.GetRunningInstanceIds(request.SourceProcessDefinitionId!, after, _options.MaxBatchSize + 1);
        if (page.Count <= _options.MaxBatchSize)
            return (page, null);
        var batch = page.Take(_options.MaxBatchSize).ToList();
        return (batch, batch[^1].ToString());
    }

    private async Task<InstanceMigrationResult> MigrateOne(Guid instanceId, string requestId, MigrationPlan plan, bool dryRun)
    {
        try
        {
            return await _grainFactory.GetGrain<IWorkflowInstanceGrain>(instanceId)
                .Migrate(MigrationId(requestId, instanceId), plan, dryRun);
        }
        catch (Exception ex)
        {
            LogInstanceFailed(ex, requestId, instanceId);
            if (!dryRun)
                FleansDiagnostics.OnInstanceMigration("failed", null);
            var error = ex is KeyNotFoundException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            return new InstanceMigrationResult(instanceId, InstanceMigrationOutcome.Failed, null,
                plan.TargetProcessDefinitionId, [], error);
        }
    }

    private static MigrationPlan ToPlan(InstanceMigrationRequest request)
        => new(request.TargetProcessDefinitionId, request.Mappings?.ToList() ?? [], request.UpdateEventTriggers);

    /// <summary>Deterministic per request and instance: a retried request is a no-op for instances it moved.</summary>
    public static string MigrationId(string requestId, Guid instanceId) => $"{requestId}:{instanceId}";

    [LoggerMessage(EventId = 14600, Level = LogLevel.Information,
        Message = "Instance migration {RequestId}: {InstanceCount} instances to {TargetProcessDefinitionId} (dry run: {DryRun})")]
    private partial void LogBatchStarted(string requestId, int instanceCount, string targetProcessDefinitionId, bool dryRun);

    [LoggerMessage(EventId = 14601, Level = LogLevel.Information,
        Message = "Instance migration {RequestId} finished: {InstanceCount} instances, {Succeeded} migrated or migratable, {Rejected} rejected, {Failed} failed")]
    private partial void LogBatchFinished(string requestId, int instanceCount, int succeeded, int rejected, int failed);

    [LoggerMessage(EventId = 14602, Level = LogLevel.Warning,
        Message = "Instance migration {RequestId} failed for instance {InstanceId}")]
    private partial void LogInstanceFailed(Exception exception, string requestId, Guid instanceId);
}
