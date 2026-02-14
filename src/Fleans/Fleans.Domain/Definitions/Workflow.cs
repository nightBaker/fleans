using System;
using System.Collections.Generic;

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain.States;

namespace Fleans.Domain
{
    public interface IWorkflowDefinition
    {
        string WorkflowId { get; }
        string? ProcessDefinitionId { get; }
        List<Activity> Activities { get; }
        List<SequenceFlow> SequenceFlows { get; }
        Activity GetActivity(string activityId);
    }
   
    public interface ICondition
    {
        bool Evaluate(WorkflowVariablesState worklfowVariablesState);
    }

    [GenerateSerializer]
    public class WorkflowDefinition : IWorkflowDefinition
    {
        [Id(0)]
        public required string WorkflowId { get; set; }

        [Id(1)]
        public required List<Activity> Activities { get; set; }

        [Id(2)]
        public required List<SequenceFlow> SequenceFlows { get; set; }

        [Id(3)]
        public string? ProcessDefinitionId { get; set; }

        public Activity GetActivity(string activityId)
            => Activities.First(a => a.ActivityId == activityId);
    }
}
