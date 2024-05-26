using System;
using System.Collections.Generic;

using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Domain
{
    public abstract class Workflow
    {
        public Guid WorkflowId { get; set; }
        public List<Activity> Activities { get; } = new List<Activity>();
        public List<SequenceFlow> SequenceFlows { get; } = new List<SequenceFlow>();

        public abstract void Define();
    }

    public class WorkflowEngine
    {
        public WorkflowInstance CurrentWorkflowInstance { get; private set; }

        public WorkflowEngine(Workflow workflow)
        {
            CurrentWorkflowInstance = new WorkflowInstance(workflow);
        }

        
    }

    public interface ICondition
    {
        bool Evaluate();
    }

    public class SimpleCondition : ICondition
    {
        private readonly Func<bool> _condition;

        public SimpleCondition(Func<bool> condition)
        {
            _condition = condition;
        }

        public bool Evaluate()
        {
            return _condition();
        }
    }

    public class TaskActivityA : Activity
    {
        public override void Execute(WorkflowInstance workflowInstance, ActivityInstance activityState)
        {
            activityState.Execute();
            Console.WriteLine("Executing Task A");
            activityState.Complete();
        }

        public override List<Activity> GetNextActivities(WorkflowInstance workflowInstance, ActivityInstance state)
        {
            var nextFlow = workflowInstance.Workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == this);
            return nextFlow != null ? new List<Activity> { nextFlow.Target } : new List<Activity>();
        }
    }
}
