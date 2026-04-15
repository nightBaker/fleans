namespace Fleans.ServiceDefaults.DTOs;

public record EvaluateConditionsResponse(List<Guid> StartedInstanceIds, List<string>? Errors = null);
