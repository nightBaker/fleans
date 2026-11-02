using System.Collections.Concurrent;
using System.Threading.Channels;
using Fleans.Application.ExternalTasks.Protocol;
using Newtonsoft.Json;

namespace Fleans.Application.ExternalTasks.Grpc;

/// <summary>
/// One open <c>Subscribe</c> stream: the <see cref="IExternalTaskWorker"/> observer the
/// dispatchers call, and the queue of <see cref="WorkerCommand"/>s that
/// <see cref="ExternalTaskWorkerGrpcService.Subscribe"/> writes to the stream (a gRPC stream takes
/// one writer at a time). Tracks the tasks assigned but not yet reported, so the service can
/// abandon them when the stream closes.
/// </summary>
internal sealed class WorkerConnection(string workerName, TimeProvider timeProvider) : IExternalTaskWorker
{
    private readonly Channel<WorkerCommand> _commands = Channel.CreateUnbounded<WorkerCommand>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, DateTimeOffset> _inFlight = new(StringComparer.Ordinal);

    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

    public string WorkerName { get; } = workerName;

    public ChannelReader<WorkerCommand> Commands => _commands.Reader;

    public Task<bool> Assign(ExternalTaskAssignment assignment)
    {
        var command = new WorkerCommand
        {
            Task = new TaskAssignment
            {
                TaskToken = assignment.TaskToken,
                TaskType = assignment.TaskType,
                WorkflowInstanceId = assignment.WorkflowInstanceId.ToString("D"),
                WorkflowId = assignment.WorkflowId,
                ActivityInstanceId = assignment.ActivityInstanceId.ToString("D"),
                ActivityId = assignment.ActivityId,
                InputsJson = assignment.InputsJson,
                VariablesJson = assignment.VariablesJson,
            },
        };
        if (assignment.ProcessDefinitionId is not null)
            command.Task.ProcessDefinitionId = assignment.ProcessDefinitionId;
        if (assignment.BusinessId is not null)
            command.Task.BusinessId = assignment.BusinessId;

        _inFlight[assignment.TaskToken] = timeProvider.GetUtcNow();
        if (_commands.Writer.TryWrite(command))
            return Task.FromResult(true);

        _inFlight.TryRemove(assignment.TaskToken, out _);
        return Task.FromResult(false);
    }

    public Task Cancel(string taskToken)
    {
        if (_inFlight.TryRemove(taskToken, out _))
            _commands.Writer.TryWrite(new WorkerCommand { Cancel = new TaskCancellation { TaskToken = taskToken } });
        return Task.CompletedTask;
    }

    public void Heartbeat() => _commands.Writer.TryWrite(new WorkerCommand { Heartbeat = new Heartbeat() });

    /// <summary>The worker reported the task's outcome through this process.</summary>
    public bool Release(string taskToken) => _inFlight.TryRemove(taskToken, out _);

    /// <summary>
    /// Forgets tasks assigned before <paramref name="cutoff"/>: their outcome was reported
    /// through another API process, or the dispatcher stopped waiting for them.
    /// </summary>
    public void ForgetAssignedBefore(DateTimeOffset cutoff)
    {
        foreach (var (token, assignedAt) in _inFlight)
        {
            if (assignedAt < cutoff)
                _inFlight.TryRemove(token, out _);
        }
    }

    /// <summary>Stops accepting tasks; returns the tokens of those still unreported.</summary>
    public IReadOnlyList<string> Close()
    {
        _commands.Writer.TryComplete();
        var unreported = _inFlight.Keys.ToList();
        _inFlight.Clear();
        return unreported;
    }
}

/// <summary>
/// The open worker connections of this process, so a <c>CompleteTask</c> / <c>FailTask</c>
/// reaching the process that holds the worker's stream releases the task there.
/// </summary>
public sealed class ExternalTaskWorkerConnections
{
    private readonly ConcurrentDictionary<string, WorkerConnection> _connections = new(StringComparer.Ordinal);

    internal void Add(WorkerConnection connection) => _connections[connection.ConnectionId] = connection;

    internal void Remove(WorkerConnection connection) => _connections.TryRemove(connection.ConnectionId, out _);

    internal void Release(string taskToken)
    {
        foreach (var connection in _connections.Values)
        {
            if (connection.Release(taskToken))
                return;
        }
    }

    /// <summary>The number of open worker streams on this process.</summary>
    public int Count => _connections.Count;
}
