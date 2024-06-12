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
        string WorkflowId { get; set; }
        List<Activity> Activities { get; }
        List<SequenceFlow> SequenceFlows { get; }
    }
   
    public interface ICondition
    {
        bool Evaluate(WorklfowVariablesState worklfowVariablesState);
    }   

    public class WorkflowDefinition : IWorkflowDefinition
    {
        public WorkflowDefinition(string workflowId, List<Activity> activities, List<SequenceFlow> sequenceFlows)
        {
            WorkflowId = workflowId;
            Activities = activities;
            SequenceFlows = sequenceFlows;
        }

        public string WorkflowId { get; set; }
        public List<Activity> Activities { get; }
        public List<SequenceFlow> SequenceFlows { get; }
    }
}
