namespace Fleans.ServiceDefaults.DTOs;

public record CompleteTaskRequest(string UserId, Dictionary<string, object?>? Variables);
