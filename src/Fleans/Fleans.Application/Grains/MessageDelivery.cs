using System.Dynamic;

namespace Fleans.Application.Grains;

/// <summary>The correlated half of a message send, shared by the plain and the deduplicated path (#743).</summary>
internal static class MessageDelivery
{
    /// <summary>
    /// Delivers <paramref name="variables"/> to the running instance subscribed to
    /// <paramref name="messageName"/> + <paramref name="correlationKey"/>. <c>false</c> when there is
    /// no key or no subscriber; a key over the length limit can have no subscriber, so it is
    /// <c>false</c> too and the send goes on to the message start events.
    /// </summary>
    public static async Task<bool> TryCorrelate(
        IGrainFactory grainFactory, string messageName, string? correlationKey, ExpandoObject variables)
    {
        var grainKey = string.IsNullOrWhiteSpace(correlationKey)
            ? null
            : MessageCorrelationKey.TryBuild(messageName, correlationKey);
        if (grainKey is null)
            return false;

        return await grainFactory.GetGrain<IMessageCorrelationGrain>(grainKey).DeliverMessage(variables);
    }
}

/// <summary>
/// The key of a <see cref="IMessageDeduplicationGrain"/>: message name + the caller's idempotency
/// key, in the format of <see cref="MessageCorrelationKey"/> (escaped key, same length limit).
/// </summary>
public static class MessageDeduplicationKey
{
    /// <summary>Throws <see cref="ArgumentException"/> (HTTP 400 at the API) for a blank or over-long key.</summary>
    public static string Build(string messageName, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("IdempotencyKey must not be empty or whitespace when provided.", nameof(idempotencyKey));

        return MessageCorrelationKey.TryBuild(messageName, idempotencyKey) ?? throw new ArgumentException(
            $"The message name plus the escaped IdempotencyKey is longer than {MessageCorrelationKey.MaxLength} characters.",
            nameof(idempotencyKey));
    }

    /// <summary>The message name of a key produced by <see cref="Build"/>.</summary>
    public static string MessageNameOf(string key) => MessageCorrelationKey.Parse(key).MessageName;
}
