using Fleans.Domain.Activities;

namespace Fleans.Domain;

public abstract class ActivityBuilder<TResult> : IActivityBuilder
{
    protected readonly List<IWorkflowConnection<IActivity, IActivity>> _connections = new List<IWorkflowConnection<IActivity, IActivity>>();

    protected ActivityBuilder(Guid id)
    {
        Id = id;
    }

    protected Guid Id { get; private set; }

    protected IActivityBuilder AddConnection(IWorkflowConnection<IActivity, IActivity> connection)
    {
        _connections.Add(connection);
        return this;
    }

    public abstract ActivityBuilderResult Build();    
}