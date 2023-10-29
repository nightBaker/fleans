namespace Fleans.Domain;

public class ExclusiveGatewayCondition: IWorkflowConnection
{
    public bool ExecuteCondition { get; }
    public ExclusiveGatewayCondition(IActivity to, bool executeCondition )
    {
        To = to;
        ExecuteCondition = executeCondition;
    }

    public IActivity From { get; set; }
    public IActivity To { get; }
    public bool CanExecute(IContext context)
    {
        if (From.IsCompleted && From is IActivity<bool> acitityResult)
        {
            return acitityResult.ExecutionResult!.Result;
        }

        return false;
    }
}