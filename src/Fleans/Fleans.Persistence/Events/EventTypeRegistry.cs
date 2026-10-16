using System.Reflection;
using Fleans.Domain.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Events;

/// <summary>
/// Serializes/deserializes domain events for the event store using GetType().Name as the discriminator.
/// Event types are discovered automatically from the two domain assemblies — no manual registration needed.
/// </summary>
public static class EventTypeRegistry
{
    private static readonly Lazy<Dictionary<string, Type>> NameToType = new(BuildTypeCache);

    // Every journaled event derives from WorkflowEvent, which lives in Fleans.Domain.
    private static Dictionary<string, Type> BuildTypeCache() =>
        typeof(WorkflowEvent).Assembly.GetTypes()
            .Where(t => typeof(WorkflowEvent).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract)
            .ToDictionary(t => t.Name);

    /// <summary>
    /// Gets the string discriminator for a journaled event using GetType().Name.
    /// </summary>
    public static string GetEventType(WorkflowEvent @event) => @event.GetType().Name;

    /// <summary>
    /// Deserializes a JSON payload into the event type identified by the discriminator.
    /// </summary>
    public static WorkflowEvent Deserialize(
        string eventType, string payload, JsonSerializerSettings settings) =>
        NameToType.Value.TryGetValue(eventType, out var type)
            ? (WorkflowEvent)JsonConvert.DeserializeObject(payload, type, settings)!
            : throw new KeyNotFoundException(
                $"Unknown event type discriminator: '{eventType}'. " +
                "No WorkflowEvent with this name found in Fleans.Domain.");
}
