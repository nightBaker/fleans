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

    private static Dictionary<string, Type> BuildTypeCache()
    {
        // Event types live across two assemblies:
        //   • Fleans.Domain.Abstractions — IDomainEvent + ExecuteCustomTaskEvent (plugin-facing surface)
        //   • Fleans.Domain               — every other concrete event (WorkflowStarted, ActivitySpawned, …)
        // Use WorkflowStarted as a stable anchor for the second assembly; it has lived in
        // Fleans.Domain.Events since the engine's first commit and is referenced by core aggregates.
        var abstractionsAssembly = typeof(IDomainEvent).Assembly;
        var domainAssembly = typeof(WorkflowStarted).Assembly;

        return new[] { abstractionsAssembly, domainAssembly }
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IDomainEvent).IsAssignableFrom(t)
                        && t.IsClass
                        && !t.IsAbstract)
            .ToDictionary(t => t.Name);
    }

    /// <summary>
    /// Gets the string discriminator for a domain event using GetType().Name.
    /// </summary>
    public static string GetEventType(IDomainEvent @event) => @event.GetType().Name;

    /// <summary>
    /// Deserializes a JSON payload into the domain event type identified by the discriminator.
    /// </summary>
    public static IDomainEvent Deserialize(
        string eventType, string payload, JsonSerializerSettings settings) =>
        NameToType.Value.TryGetValue(eventType, out var type)
            ? (IDomainEvent)JsonConvert.DeserializeObject(payload, type, settings)!
            : throw new KeyNotFoundException(
                $"Unknown event type discriminator: '{eventType}'. " +
                "No IDomainEvent implementation with this name found in Fleans.Domain or Fleans.Domain.Abstractions.");
}
