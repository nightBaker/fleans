using Fleans.Domain.Activities;

namespace Fleans.Domain.Connections;

public interface IWorkflowErrorConecction
{
    bool CanExecute(IContext context, Exception exception);
    IActivity To { get; }
}