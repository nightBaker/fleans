namespace Fleans.ServiceDefaults.DTOs;

/// <param name="UserId">
/// Acting user. Required when auth is disabled. Under JWT auth the user comes from the
/// token; this field is optional and, if sent, must match the token (#793).
/// </param>
public record CompleteTaskRequest(string? UserId, Dictionary<string, object?>? Variables);
