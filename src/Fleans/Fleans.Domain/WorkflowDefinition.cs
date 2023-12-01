using Fleans.Domain.Activities;
using Fleans.Domain.Connections;

namespace Fleans.Domain;

public record WorkflowDefinition
{
    public WorkflowDefinition(Guid id, int version, 
        IStartProcessEventActivity[] startEvents,
        IExecutableActivity[] activities,
        Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]> connections)
    {
        this.Id = id;
        this.Version = version;
        this.StartEvents = startEvents;
        this.Activities = activities;
        this.Connections = connections;
        
        if (!startEvents.Any())
        {
            throw new ArgumentException("Workflow should start with at lease one event");
        }
        StartEvents = startEvents;
    }

    public Guid Id { get; init; }
    public int Version { get; init; }
    public IStartProcessEventActivity[] StartEvents { get; init; }
    public IActivity[] Activities { get; init; }
    public Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]> Connections { get; init; }
}
