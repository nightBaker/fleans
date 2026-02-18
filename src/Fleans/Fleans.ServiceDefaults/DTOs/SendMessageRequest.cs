using System.Dynamic;

namespace Fleans.ServiceDefaults.DTOs;

public record SendMessageRequest(string MessageName, string CorrelationKey, ExpandoObject? Variables);
