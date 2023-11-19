namespace Fleans.Domain;

public interface IWorkflowErrorConecction<out FromType, out ToType> 
    : IWorkflowConnection<FromType, ToType> where FromType : IActivity where ToType : IActivity
{
    bool CanExecute(IContext context, Exception exception);
}