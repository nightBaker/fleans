namespace Fleans.ServiceDefaults.DTOs;

public record SendSignalResponse(int DeliveredCount, List<Guid>? WorkflowInstanceIds = null);
