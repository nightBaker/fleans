namespace Fleans.ServiceDefaults.DTOs;

public record SendMessageResponse(bool Delivered, List<Guid>? WorkflowInstanceIds = null);
