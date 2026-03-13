using System.Dynamic;

namespace Fleans.ServiceDefaults.DTOs;

public record CompleteTaskRequest(string UserId, ExpandoObject? Variables);
