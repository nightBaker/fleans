using System.Dynamic;

namespace Fleans.ServiceDefaults.DTOs;

/// <param name="BusinessId">Optional business identifier for instances this message <em>starts</em>
/// (message start events); ignored when the message correlates to a running instance (#824).</param>
/// <param name="IdempotencyKey">Optional key that makes the send safe to retry (#743): a repeat with
/// the same message name and key within 24 hours does nothing and returns the first send's outcome —
/// the same <c>WorkflowInstanceIds</c>, or <c>Delivered</c> for a correlated delivery.</param>
public record SendMessageRequest(
    string MessageName,
    string? CorrelationKey,
    ExpandoObject? Variables,
    string? BusinessId = null,
    string? IdempotencyKey = null);
