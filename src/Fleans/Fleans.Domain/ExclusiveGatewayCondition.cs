namespace Fleans.Domain;

public class ExclusiveGatewayConnection: IWorkflowConnection<ExclusiveGatewayActivity, IActivity>
{
    public bool ExecuteCondition { get; }
    public ExclusiveGatewayActivity From { get; set; }
    public IActivity To { get; }

    public ExclusiveGatewayConnection(ExclusiveGatewayActivity from, IActivity to, bool executeCondition )
    {
        From = from;
        To = to;
        ExecuteCondition = executeCondition;
    }    

    public bool CanExecute(IContext context)
    {
        if (From.IsCompleted)
        {
            return From.Condition.Result == ExecuteCondition;
        }

        return false;
    }
}