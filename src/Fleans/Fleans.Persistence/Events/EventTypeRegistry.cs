using System.Reflection;
using Fleans.Domain.Events;
using Newtonsoft.Json;

namespace Fleans.Persistence.Events;

/// <summary>
/// Serializes/deserializes domain events for the event store using GetType().Name as the discriminator.
/// Event types are discovered automatically from the domain assembly — no manual registration needed.
/// </summary>
public static class EventTypeRegistry
{
    private static readonly Lazy<Dictionary<string, Type>> NameToType = new(BuildTypeCache);

    private static Dictionary<string, Type> BuildTypeCache()
    {
        var domainAssembly = typeof(IDomainEvent).Assembly;
        return domainAssembly.GetTypes()
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
                "No IDomainEvent implementation with this name found in the domain assembly.");
}
