namespace Fleans.Domain;

public abstract class ActivityBuilder<TResult> : IActivityBuilder
{
    protected readonly List<IWorkflowConnection> _connections = new List<IWorkflowConnection>();
    
    protected IActivityBuilder AddConnection(IWorkflowConnection connection)
    {
        _connections.Add(connection);
        return this;
    }

    public abstract IActivity Build(Guid id);
}