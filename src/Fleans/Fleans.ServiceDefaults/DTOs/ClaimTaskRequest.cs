namespace Fleans.ServiceDefaults.DTOs;

public record ClaimTaskRequest(string UserId, IReadOnlyList<string>? UserGroups = null);
