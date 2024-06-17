﻿

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record TaskActivity : Activity
{
    public TaskActivity(string ActivityId) : base(ActivityId)
    {
    }

    internal override async Task<List<Activity>> GetNextActivities(IWorkflowInstance workflowInstance, IActivityInstance state)
    {
        var definition = await workflowInstance.GetWorkflowDefinition();
        var nextFlow = definition.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
        return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
    }
}
