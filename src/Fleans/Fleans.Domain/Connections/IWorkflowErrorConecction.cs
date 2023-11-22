namespace Fleans.Domain;

public interface IWorkflowErrorConecction
{
    bool CanExecute(IContext context, Exception exception);
    IActivity To { get; }
}