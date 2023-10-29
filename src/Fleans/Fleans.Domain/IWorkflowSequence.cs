namespace Fleans.Domain
{
    public interface IWorkflowConnection : IDictionary<Guid, Guid>
    {
        IActivity From { get; }
        IActivity To { get; }

        bool CanExecute(IContext context);
    }
}