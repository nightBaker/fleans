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
    public abstract class Workflow
    {
        public string WorkflowId { get; set; }
        public List<Activity> Activities { get; } = new List<Activity>();
        public List<SequenceFlow> SequenceFlows { get; } = new List<SequenceFlow>();
    }
   
    public interface ICondition
    {
        bool Evaluate(WorklfowVariablesState worklfowVariablesState);
    }   
}
