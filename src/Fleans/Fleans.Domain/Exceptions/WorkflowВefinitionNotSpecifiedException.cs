namespace Fleans.Domain.Exceptions;

public class WorkflowВefinitionNotSpecifiedException : InvalidOperationException
{
    public WorkflowВefinitionNotSpecifiedException()
    {
    }

    public WorkflowВefinitionNotSpecifiedException(string message)
        : base(message)
    {
    }

    public WorkflowВefinitionNotSpecifiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}