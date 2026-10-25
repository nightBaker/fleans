using System.Dynamic;

namespace Fleans.Application.Grains;

/// <summary>
/// One message name + idempotency key (<see cref="MessageDeduplicationKey"/>, #743). Activated only
/// for sends that carry an idempotency key; a send without one never touches it.
/// </summary>
public interface IMessageDeduplicationGrain : IGrainWithStringKey
{
    /// <summary>
    /// Sends the message once: correlates it to a running instance, or else starts the processes
    /// with a matching message start event — unless a send with this key already did, in which case
    /// it returns that send's outcome and does nothing. Calls for one key run one at a time.
    /// </summary>
    Task<SendMessageResult> Send(string? correlationKey, ExpandoObject variables, string? businessId);
}
