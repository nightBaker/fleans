namespace Fleans.Domain;

public record WorkflowDefinition(Guid Id, int Version, IActivity[] Activities, Dictionary<Guid, IWorkflowConnection<IActivity, IActivity>[]> Connections);
