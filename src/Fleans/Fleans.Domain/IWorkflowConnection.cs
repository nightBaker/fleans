namespace Fleans.Domain;

public interface IWorkflowConnection
{
    IActivity From { get; internal set; }
    IActivity To { get; }

    bool CanExecute(IContext context);
}