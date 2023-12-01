using Fleans.Domain.Activities;

namespace Fleans.Domain.Connections;

public interface IWorkflowConnection<out FromType, out ToType> where FromType : IActivity where ToType : IActivity
{
    FromType From { get; }
    ToType To { get; }
    bool CanExecute(IContext context);
}
