using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;

namespace Fleans.Application.ExternalTasks;

/// <inheritdoc cref="IExternalTaskWorkerPoolGrain"/>
/// <remarks>Reentrant: every method is synchronous, so turns never interleave mid-update.</remarks>
[Reentrant]
public sealed partial class ExternalTaskWorkerPoolGrain : Grain, IExternalTaskWorkerPoolGrain
{
    private readonly Dictionary<string, (IExternalTaskWorker Worker, DateTimeOffset RenewedAt)> _workers = new(StringComparer.Ordinal);
    private readonly ExternalTaskOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ExternalTaskWorkerPoolGrain> _logger;

    public ExternalTaskWorkerPoolGrain(
        IOptions<ExternalTaskOptions> options,
        TimeProvider timeProvider,
        ILogger<ExternalTaskWorkerPoolGrain> logger)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The pool key of <paramref name="taskType"/>: task types match case-insensitively.</summary>
    public static string KeyFor(string taskType) => taskType.ToLowerInvariant();

    public Task Join(string connectionId, IExternalTaskWorker worker)
    {
        var joined = !_workers.ContainsKey(connectionId);
        _workers[connectionId] = (worker, _timeProvider.GetUtcNow());
        if (joined)
            LogJoined(connectionId, this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task Leave(string connectionId)
    {
        if (_workers.Remove(connectionId))
            LogLeft(connectionId, this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task<List<IExternalTaskWorker>> GetWorkers()
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.WorkerTimeout;
        foreach (var expired in _workers.Where(kv => kv.Value.RenewedAt < cutoff).Select(kv => kv.Key).ToList())
        {
            _workers.Remove(expired);
            LogExpired(expired, this.GetPrimaryKeyString());
        }
        return Task.FromResult(_workers.Values.Select(w => w.Worker).ToList());
    }

    [LoggerMessage(EventId = 14530, Level = LogLevel.Information,
        Message = "Worker connection {ConnectionId} joined the pool of external-task type '{TaskType}'")]
    private partial void LogJoined(string connectionId, string taskType);

    [LoggerMessage(EventId = 14531, Level = LogLevel.Information,
        Message = "Worker connection {ConnectionId} left the pool of external-task type '{TaskType}'")]
    private partial void LogLeft(string connectionId, string taskType);

    [LoggerMessage(EventId = 14532, Level = LogLevel.Warning,
        Message = "Worker connection {ConnectionId} dropped from the pool of external-task type '{TaskType}': not renewed within the worker timeout")]
    private partial void LogExpired(string connectionId, string taskType);
}
