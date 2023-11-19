namespace Fleans.Domain;

public interface IWorkflowError
{
    string Message { get; }
    WorkflowErrorCode Code { get; }
}
