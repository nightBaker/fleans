namespace Fleans.ServiceDefaults.DTOs;

public record UploadBpmnResponse(
    string Message,
    string WorkflowId,
    int ActivitiesCount,
    int SequenceFlowsCount);

