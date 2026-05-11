namespace Fleans.ServiceDefaults.DTOs;

public record FailTaskRequest(string ErrorMessage, string ErrorCode = "500");
