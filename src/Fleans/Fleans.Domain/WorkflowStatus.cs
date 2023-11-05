namespace Fleans.Domain;

public partial class Workflow
{
    public enum WorkflowStatus
    {
        Running,
        Completed,
        Failed,
        Waiting
    }
}
