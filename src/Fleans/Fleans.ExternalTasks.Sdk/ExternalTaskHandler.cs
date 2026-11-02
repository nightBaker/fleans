using System.Dynamic;

namespace Fleans.ExternalTasks.Sdk;

/// <summary>
/// Base class for an external-task worker handler: the code behind <c>&lt;serviceTask type="…"&gt;</c>.
/// Register a subclass with <see cref="ExternalTaskServiceCollectionExtensions.AddExternalTaskHandler{THandler}"/>;
/// the worker resolves a new instance from a fresh DI scope for every task, so constructor
/// injection works and no state survives between tasks.
/// </summary>
public abstract class ExternalTaskHandler
{
    /// <summary>
    /// Runs one task. <paramref name="resolvedInputs"/> holds the values of the task's
    /// <c>&lt;fleans:input&gt;</c> mappings, which the engine evaluated before dispatch;
    /// <paramref name="variables"/> the workflow variables visible to the task. The returned
    /// dictionary is the task's result: the engine evaluates the <c>&lt;fleans:output&gt;</c>
    /// mappings against it (so write the fields the outputs reference, e.g. <c>__response</c>),
    /// or merges all of it into the workflow scope when the task has none. It crosses the wire as
    /// JSON, so values must be JSON-serializable.
    ///
    /// Throw <see cref="Fleans.Domain.Errors.CustomTaskFailedActivityException"/> to fail the
    /// task with a BPMN error code; any other exception fails it with code <c>500</c>.
    /// <paramref name="cancellationToken"/> fires when the engine stops waiting for the task
    /// (timeout, engine shutdown), the worker loses its connection, or the host stops; the
    /// task is then delivered again, so an <see cref="OperationCanceledException"/> from it is
    /// not reported as a failure. Delivery is at-least-once: key external side effects by
    /// <see cref="ExternalTaskContext.ActivityInstanceId"/>.
    /// </summary>
    public abstract Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Per-task metadata passed to <see cref="ExternalTaskHandler.ExecuteAsync"/> so handlers can
/// correlate work with the specific workflow + activity instance — for idempotency keys,
/// distributed-tracing tags and deduplication keys against external systems.
/// <see cref="ActivityInstanceId"/> stays the same when a task is delivered again.
/// <see cref="BusinessId"/> is the instance's business identifier set at start (an order
/// number, a contract id), or <c>null</c> when the instance was started without one (#824).
/// </summary>
public sealed record ExternalTaskContext(
    Guid WorkflowInstanceId,
    string WorkflowId,
    string? ProcessDefinitionId,
    Guid ActivityInstanceId,
    string ActivityId,
    string TaskType,
    string? BusinessId = null);
