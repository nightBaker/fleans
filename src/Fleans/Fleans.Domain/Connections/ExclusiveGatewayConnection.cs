using Fleans.Domain.Activities;

namespace Fleans.Domain.Connections;

public class ExclusiveGatewayConnection : IWorkflowConnection<ExclusiveGatewayActivity, IActivity>
{
    private bool ExecuteCondition { get; }
    public ExclusiveGatewayActivity From { get; set; }
    public IActivity To { get; }

    public ExclusiveGatewayConnection(ExclusiveGatewayActivity from, IActivity to, bool executeCondition)
    {
        From = from;
        To = to;
        ExecuteCondition = executeCondition;
    }

    public bool CanExecute(IContext context)
    {
        if (From.IsCompleted)
        {
            return From.ExecutionResult?.Result == ExecuteCondition;
        }

        return false;
    }
}
