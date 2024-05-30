﻿

namespace Fleans.Domain.Activities
{
    public class TaskActivity : Activity
    {
        internal override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance state)
        {
            var nextFlow = workflowInstance.Workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
