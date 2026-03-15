using Fleans.Domain.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Events;

/// <summary>
/// Maps domain event types to string discriminators for event store serialization.
/// Covers the 22 state-mutation domain events processed by WorkflowExecution.Apply().
/// </summary>
public static class EventTypeRegistry
{
    private static readonly Dictionary<Type, string> TypeToName = new()
    {
        // Workflow lifecycle
        [typeof(WorkflowStarted)] = nameof(WorkflowStarted),
        [typeof(ExecutionStarted)] = nameof(ExecutionStarted),
        [typeof(WorkflowCompleted)] = nameof(WorkflowCompleted),

        // Activity lifecycle
        [typeof(ActivitySpawned)] = nameof(ActivitySpawned),
        [typeof(ActivityExecutionStarted)] = nameof(ActivityExecutionStarted),
        [typeof(ActivityCompleted)] = nameof(ActivityCompleted),
        [typeof(ActivityFailed)] = nameof(ActivityFailed),
        [typeof(ActivityExecutionReset)] = nameof(ActivityExecutionReset),
        [typeof(ActivityCancelled)] = nameof(ActivityCancelled),
        [typeof(MultiInstanceTotalSet)] = nameof(MultiInstanceTotalSet),

        // Variable management
        [typeof(VariablesMerged)] = nameof(VariablesMerged),
        [typeof(ChildVariableScopeCreated)] = nameof(ChildVariableScopeCreated),
        [typeof(VariableScopeCloned)] = nameof(VariableScopeCloned),
        [typeof(VariableScopesRemoved)] = nameof(VariableScopesRemoved),

        // Gateway/token management
        [typeof(ConditionSequencesAdded)] = nameof(ConditionSequencesAdded),
        [typeof(ConditionSequenceEvaluated)] = nameof(ConditionSequenceEvaluated),
        [typeof(GatewayForkCreated)] = nameof(GatewayForkCreated),
        [typeof(GatewayForkTokenAdded)] = nameof(GatewayForkTokenAdded),
        [typeof(GatewayForkRemoved)] = nameof(GatewayForkRemoved),

        // Parent/child
        [typeof(ParentInfoSet)] = nameof(ParentInfoSet),
        [typeof(ChildWorkflowLinked)] = nameof(ChildWorkflowLinked),

        // Timer cycle tracking
        [typeof(TimerCycleUpdated)] = nameof(TimerCycleUpdated),
    };

    private static readonly Dictionary<string, Type> NameToType =
        TypeToName.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Gets the string discriminator for a domain event.
    /// </summary>
    public static string GetEventType(IDomainEvent @event) =>
        TypeToName.TryGetValue(@event.GetType(), out var name)
            ? name
            : throw new InvalidOperationException(
                $"Unknown domain event type: {@event.GetType().FullName}. " +
                "Ensure it is registered in EventTypeRegistry.");

    /// <summary>
    /// Deserializes a JSON payload into the domain event type identified by the discriminator.
    /// </summary>
    public static IDomainEvent Deserialize(
        string eventType, string payload, JsonSerializerSettings settings) =>
        NameToType.TryGetValue(eventType, out var type)
            ? (IDomainEvent)JsonConvert.DeserializeObject(payload, type, settings)!
            : throw new KeyNotFoundException(
                $"Unknown event type discriminator: '{eventType}'. " +
                "The event store contains an event type not registered in EventTypeRegistry.");

    /// <summary>
    /// Returns all registered event type names (for testing/diagnostics).
    /// </summary>
    public static IReadOnlyCollection<string> RegisteredEventTypes => NameToType.Keys;

    /// <summary>
    /// Returns all registered CLR types (for testing/diagnostics).
    /// </summary>
    public static IReadOnlyCollection<Type> RegisteredClrTypes => TypeToName.Keys;
}
