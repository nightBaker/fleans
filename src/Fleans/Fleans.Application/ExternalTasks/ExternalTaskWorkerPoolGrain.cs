using Fleans.Application.CustomTasks;
using Fleans.Application.Grains;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;

namespace Fleans.Application.ExternalTasks;

/// <inheritdoc cref="IExternalTaskWorkerPoolGrain"/>
/// <remarks>Reentrant: every method is synchronous, so turns never interleave mid-update.</remarks>
[Reentrant]
public sealed partial class ExternalTaskWorkerPoolGrain : Grain, IExternalTaskWorkerPoolGrain
{
    private readonly Dictionary<string, (ExternalTaskPoolWorker Worker, DateTimeOffset RenewedAt)> _workers = new(StringComparer.Ordinal);
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

    /// <summary>
    /// The pool of the <c>scheme:*</c> wildcard covering <paramref name="taskType"/>, or <c>null</c>
    /// when the type has no scheme.
    /// </summary>
    public static string? WildcardKeyFor(string taskType)
    {
        var colon = taskType.IndexOf(':');
        return colon > 0 ? KeyFor(taskType[..(colon + 1)] + "*") : null;
    }

    public Task<bool> Join(string connectionId, IExternalTaskWorker worker, VariableSelection variables,
        string? version = null, CustomTaskParameterSchema? schema = null)
    {
        DropExpired();

        // Only live connections count: a stale catalog row must not block a fixed worker.
        if (version is not null && _workers.Any(kv => kv.Key != connectionId
                && kv.Value.Worker.Version == version
                && !CustomTaskParameterSchema.HaveSameContract(kv.Value.Worker.ParameterSchema, schema)))
        {
            LogRefused(connectionId, this.GetPrimaryKeyString(), version);
            return Task.FromResult(false);
        }

        var joined = !_workers.ContainsKey(connectionId);
        _workers[connectionId] = (new ExternalTaskPoolWorker(worker, variables, version, schema), _timeProvider.GetUtcNow());
        if (joined)
            LogJoined(connectionId, this.GetPrimaryKeyString());
        return Task.FromResult(true);
    }

    public Task Leave(string connectionId)
    {
        if (_workers.Remove(connectionId))
            LogLeft(connectionId, this.GetPrimaryKeyString());
        return Task.CompletedTask;
    }

    public Task<List<ExternalTaskPoolWorker>> GetWorkers()
    {
        DropExpired();
        return Task.FromResult(_workers.Values.Select(w => w.Worker).ToList());
    }

    private void DropExpired()
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.WorkerTimeout;
        foreach (var expired in _workers.Where(kv => kv.Value.RenewedAt < cutoff).Select(kv => kv.Key).ToList())
        {
            _workers.Remove(expired);
            LogExpired(expired, this.GetPrimaryKeyString());
        }
    }

    [LoggerMessage(EventId = 14533, Level = LogLevel.Warning,
        Message = "Worker connection {ConnectionId} refused by the pool of external-task type '{TaskType}': version {Version} is connected with different parameters")]
    private partial void LogRefused(string connectionId, string taskType, string version);

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
