namespace Fleans.Domain;

public class ExclusiveGatewayCondition: IWorkflowConnection
{
    public bool ExecuteCondition { get; }
    public ExclusiveGatewayCondition(IActivity to, bool exectudeCondition )
    {
        To = to;
        ExecuteCondition = exectudeCondition;
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