using System.Dynamic;

namespace Fleans.ServiceDefaults.DTOs;

/// <param name="BusinessId">Optional business identifier for instances this message <em>starts</em>
/// (message start events); ignored when the message correlates to a running instance (#824).</param>
public record SendMessageRequest(string MessageName, string? CorrelationKey, ExpandoObject? Variables, string? BusinessId = null);
