using Fleans.Domain.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Events;

/// <summary>
/// Maps domain event types to string discriminators for event store serialization.
/// Uses GetType().Name as the discriminator, with a registry of known types for deserialization.
/// Covers the 26 state-mutation domain events processed by WorkflowExecution.Apply().
/// </summary>
public static class EventTypeRegistry
{
    private static readonly HashSet<Type> RegisteredTypes =
    [
        // Workflow lifecycle
        typeof(WorkflowStarted),
        typeof(ExecutionStarted),
        typeof(WorkflowCompleted),

        // Activity lifecycle
        typeof(ActivitySpawned),
        typeof(ActivityExecutionStarted),
        typeof(ActivityCompleted),
        typeof(ActivityFailed),
        typeof(ActivityExecutionReset),
        typeof(ActivityCancelled),
        typeof(MultiInstanceTotalSet),

        // Variable management
        typeof(VariablesMerged),
        typeof(ChildVariableScopeCreated),
        typeof(VariableScopeCloned),
        typeof(VariableScopesRemoved),

        // Gateway/token management
        typeof(ConditionSequencesAdded),
        typeof(ConditionSequenceEvaluated),
        typeof(GatewayForkCreated),
        typeof(GatewayForkTokenAdded),
        typeof(GatewayForkRemoved),

        // Parent/child
        typeof(ParentInfoSet),
        typeof(ChildWorkflowLinked),

        // User task lifecycle
        typeof(UserTaskRegistered),
        typeof(UserTaskClaimed),
        typeof(UserTaskUnclaimed),
        typeof(UserTaskUnregistered),

        // Timer cycle tracking
        typeof(TimerCycleUpdated),
    ];

    private static readonly Dictionary<string, Type> NameToType =
        RegisteredTypes.ToDictionary(t => t.Name);

    /// <summary>
    /// Gets the string discriminator for a domain event using GetType().Name.
    /// </summary>
    public static string GetEventType(IDomainEvent @event)
    {
        var type = @event.GetType();
        return RegisteredTypes.Contains(type)
            ? type.Name
            : throw new InvalidOperationException(
                $"Unknown domain event type: {type.FullName}. " +
                "Ensure it is registered in EventTypeRegistry.");
    }

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
    public static IReadOnlyCollection<Type> RegisteredClrTypes => RegisteredTypes;
}
