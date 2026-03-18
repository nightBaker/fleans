using System.Collections.Frozen;
using Fleans.Domain.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Events;

/// <summary>
/// Maps domain event types to string discriminators for envelope-based event storage.
/// All IDomainEvent implementations must be registered here.
/// </summary>
public static class EventTypeRegistry
{
    private static readonly FrozenDictionary<string, Type> NameToType;
    private static readonly FrozenDictionary<Type, string> TypeToName;

    static EventTypeRegistry()
    {
        var mappings = new Dictionary<string, Type>
        {
            // Workflow lifecycle
            ["WorkflowStarted"] = typeof(WorkflowStarted),
            ["ExecutionStarted"] = typeof(ExecutionStarted),
            ["WorkflowCompleted"] = typeof(WorkflowCompleted),

            // Activity lifecycle
            ["ActivitySpawned"] = typeof(ActivitySpawned),
            ["ActivityExecutionStarted"] = typeof(ActivityExecutionStarted),
            ["ActivityCompleted"] = typeof(ActivityCompleted),
            ["ActivityFailed"] = typeof(ActivityFailed),
            ["ActivityExecutionReset"] = typeof(ActivityExecutionReset),
            ["ActivityCancelled"] = typeof(ActivityCancelled),
            ["MultiInstanceTotalSet"] = typeof(MultiInstanceTotalSet),

            // Variable management
            ["VariablesMerged"] = typeof(VariablesMerged),
            ["ChildVariableScopeCreated"] = typeof(ChildVariableScopeCreated),
            ["VariableScopeCloned"] = typeof(VariableScopeCloned),
            ["VariableScopesRemoved"] = typeof(VariableScopesRemoved),

            // Gateway/token management
            ["ConditionSequencesAdded"] = typeof(ConditionSequencesAdded),
            ["ConditionSequenceEvaluated"] = typeof(ConditionSequenceEvaluated),
            ["GatewayForkCreated"] = typeof(GatewayForkCreated),
            ["GatewayForkTokenAdded"] = typeof(GatewayForkTokenAdded),
            ["GatewayForkRemoved"] = typeof(GatewayForkRemoved),

            // Parent/child
            ["ParentInfoSet"] = typeof(ParentInfoSet),
            ["ChildWorkflowLinked"] = typeof(ChildWorkflowLinked),

            // User task lifecycle
            ["UserTaskRegistered"] = typeof(UserTaskRegistered),
            ["UserTaskClaimed"] = typeof(UserTaskClaimed),
            ["UserTaskUnclaimed"] = typeof(UserTaskUnclaimed),
            ["UserTaskUnregistered"] = typeof(UserTaskUnregistered),

            // Timer cycle tracking
            ["TimerCycleUpdated"] = typeof(TimerCycleUpdated),
        };

        NameToType = mappings.ToFrozenDictionary();
        TypeToName = mappings.ToFrozenDictionary(kvp => kvp.Value, kvp => kvp.Key);
    }

    /// <summary>
    /// Gets the string discriminator for a domain event type.
    /// </summary>
    public static string GetEventTypeName(IDomainEvent domainEvent) =>
        TypeToName[domainEvent.GetType()];

    /// <summary>
    /// Gets the string discriminator for a domain event CLR type.
    /// </summary>
    public static string GetEventTypeName<T>() where T : IDomainEvent =>
        TypeToName[typeof(T)];

    /// <summary>
    /// Gets the CLR type for a string discriminator.
    /// </summary>
    public static Type GetEventType(string eventTypeName) =>
        NameToType[eventTypeName];

    /// <summary>
    /// Deserializes a JSON payload into the correct domain event type.
    /// </summary>
    public static IDomainEvent Deserialize(string eventTypeName, string payload, JsonSerializerSettings settings)
    {
        var type = GetEventType(eventTypeName);
        return (IDomainEvent)JsonConvert.DeserializeObject(payload, type, settings)!;
    }

    /// <summary>
    /// Serializes a domain event to JSON.
    /// </summary>
    public static string Serialize(IDomainEvent domainEvent, JsonSerializerSettings settings) =>
        JsonConvert.SerializeObject(domainEvent, settings);

    /// <summary>
    /// Returns all registered event type names.
    /// </summary>
    public static IReadOnlyCollection<string> AllEventTypeNames => NameToType.Keys;

    /// <summary>
    /// Returns all registered event CLR types.
    /// </summary>
    public static IReadOnlyCollection<Type> AllEventTypes => TypeToName.Keys;
}
