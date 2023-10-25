namespace Fleans.Domain
{
    public interface IWorkflowSequence : IDictionary<Guid, Guid>
    {
        IActivity[] From { get; }
        IActivity[] To { get; }

        bool CanExecute(IContext context);
    }
}