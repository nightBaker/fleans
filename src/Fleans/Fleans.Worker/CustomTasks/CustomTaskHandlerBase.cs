using System.Dynamic;
using Fleans.Application.Abstractions.Events;
using Fleans.Application.CustomTasks;
using Fleans.Application.Grains;
using Fleans.Application.Logging;
using Fleans.Domain.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Worker.CustomTasks;

/// <summary>
/// Base class for plugin-supplied custom-task handlers. Subscribes to the shared
/// <c>events/ExecuteCustomTaskEvent</c> stream, filters incoming events by <c>TaskType</c>,
/// resolves input mappings, invokes the plugin's <see cref="ExecuteAsync"/>, projects
/// outputs, and calls <see cref="IWorkflowInstanceCallback.CompleteActivity"/> /
/// <see cref="IWorkflowInstanceCallback.FailActivity"/> using the same shape as
/// <c>WorkflowExecuteScriptEventHandler</c>.
///
/// Plugin authors override <see cref="TaskType"/> and <see cref="ExecuteAsync"/>; the
/// stream subscription, filtering, error path, and completion callback are all owned
/// by the base.
/// </summary>
[ImplicitStreamSubscription(WorkflowEventStreams.ExecuteCustomTaskStreamNamespace)]
public abstract partial class CustomTaskHandlerBase : Grain, IGrainWithStringKey, IAsyncObserver<ExecuteCustomTaskEvent>
{
    private readonly ILogger _logger;
    private readonly IGrainFactory _grainFactory;

    protected CustomTaskHandlerBase(ILogger logger, IGrainFactory grainFactory)
    {
        _logger = logger;
        _grainFactory = grainFactory;
    }

    /// <summary>The BPMN <c>type="…"</c> discriminator this handler claims.</summary>
    protected abstract string TaskType { get; }

    /// <summary>
    /// Executes the plugin's work. Inputs are resolved from the workflow scope per
    /// <c>&lt;zeebe:input&gt;</c> mappings; the returned dictionary feeds output mapping
    /// (so plugins should write fields the outputs reference, e.g. <c>__response</c>).
    /// The <paramref name="context"/> carries identifiers a plugin may need for
    /// idempotency keys, distributed-tracing tags, or external-system dedup.
    /// </summary>
    protected abstract Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken);

    // Test-only hook fired after handles are resumed. Null in production.
    internal static Action<int>? OnImplicitActivation;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider(WorkflowEventStreams.StreamProvider);
        // Stream key matches the grain's primary key — set by Orleans implicit-subscription
        // dispatch to the workflow-instance Guid the publisher used.
        var streamId = StreamId.Create(WorkflowEventStreams.ExecuteCustomTaskStreamNamespace, this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<ExecuteCustomTaskEvent>(streamId);

        var handles = await stream.GetAllSubscriptionHandles();
        foreach (var handle in handles)
            await handle.ResumeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

        var siloDetails = this.ServiceProvider.GetRequiredService<ILocalSiloDetails>();
        LogActivated(siloDetails.Name, this.GetPrimaryKeyString());
        OnImplicitActivation?.Invoke(handles.Count);

        await base.OnActivateAsync(cancellationToken);
    }

    public async Task OnNextAsync(ExecuteCustomTaskEvent item, StreamSequenceToken? token = null)
    {
        if (!string.Equals(item.TaskType, TaskType, StringComparison.OrdinalIgnoreCase))
            return;

        using var scope = WorkflowLoggingContext.BeginWorkflowScope(
            _logger, item.WorkflowId, item.ProcessDefinitionId, item.WorkflowInstanceId, item.ActivityId);

        LogHandling(item.ActivityId, item.TaskType);

        var workflowInstance = _grainFactory.GetGrain<IWorkflowInstanceCallback>(item.WorkflowInstanceId);

        try
        {
            var variables = await workflowInstance.GetVariables(item.VariablesId);

            var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var im in item.InputMappings)
                resolved[im.Target] = MappingResolver.Resolve(im.Source, (IDictionary<string, object?>)variables);

            var context = new CustomTaskExecutionContext(
                item.WorkflowInstanceId,
                item.WorkflowId,
                item.ProcessDefinitionId,
                item.ActivityInstanceId,
                item.ActivityId,
                item.TaskType);

            var pluginResult = await ExecuteAsync(resolved, variables, context, CancellationToken.None);

            var outputs = new ExpandoObject();
            var outputsDict = (IDictionary<string, object?>)outputs;
            foreach (var om in item.OutputMappings)
                outputsDict[om.Target] = MappingResolver.Resolve(om.Source, pluginResult);

            await workflowInstance.CompleteActivity(item.ActivityId, item.ActivityInstanceId, outputs);
        }
        catch (Exception ex)
        {
            LogExecuteFailed(ex, item.ActivityId, item.TaskType);
            try
            {
                await workflowInstance.FailActivity(item.ActivityId, item.ActivityInstanceId, ex);
            }
            catch (Exception failEx)
            {
                LogFailActivityFailed(failEx, item.ActivityId);
                throw; // let stream provider retry — domain idempotency guards handle duplicates
            }
        }
    }

    public Task OnCompletedAsync()
    {
        LogStreamCompleted();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        LogStreamError(ex);
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 4030, Level = LogLevel.Information,
        Message = "Handling custom-task event for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogHandling(string activityId, string taskType);

    [LoggerMessage(EventId = 4035, Level = LogLevel.Information,
        Message = "Custom-task handler activated on silo {SiloName} for stream key {StreamKey}")]
    private partial void LogActivated(string siloName, string streamKey);

    [LoggerMessage(EventId = 4031, Level = LogLevel.Error,
        Message = "Custom-task plugin execution failed for activity {ActivityId} (taskType={TaskType})")]
    private partial void LogExecuteFailed(Exception ex, string activityId, string taskType);

    [LoggerMessage(EventId = 4032, Level = LogLevel.Information,
        Message = "Custom-task event stream completed")]
    private partial void LogStreamCompleted();

    [LoggerMessage(EventId = 4033, Level = LogLevel.Error,
        Message = "Custom-task event stream error")]
    private partial void LogStreamError(Exception ex);

    [LoggerMessage(EventId = 4034, Level = LogLevel.Critical,
        Message = "FailActivity call itself failed for activity {ActivityId} — workflow may be stalled")]
    private partial void LogFailActivityFailed(Exception ex, string activityId);
}
