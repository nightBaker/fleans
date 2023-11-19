namespace Fleans.Domain.Connections;

public class OnErrorConnection : IWorkflowErrorConecction<IActivity, IActivity>
{
    public IActivity From { get; }
    public IActivity To { get; }
    public IConditionExpressionRunner Condition { get; }

    public OnErrorConnection(IActivity from, IActivity to, IConditionExpressionRunner condition)
    {
        From = from;
        To = to;
        Condition = condition;
    }

    public bool CanExecute(IContext context)
    {
        throw new NotImplementedException("Error connection can execute only errors, use CanExecute(IContext context, Exception exception)");
    }

    public bool CanExecute(IContext context, Exception exception)
    {
        return Condition.Evaluate(context, exception);
    }
}