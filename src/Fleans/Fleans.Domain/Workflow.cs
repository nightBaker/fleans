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
        private readonly Dictionary<string, Workflow> _workflows = new Dictionary<string, Workflow>();

        public void DefineWorkflow(string key, Workflow workflow)
        {
            workflow.Define();
            _workflows[key] = workflow;
        }

        public WorkflowInstance CreateWorkflowInstance(string key)
        {
            if (!_workflows.ContainsKey(key))
                throw new ArgumentException("Workflow not found");

            var workflow = _workflows[key];
            return new WorkflowInstance(workflow);
        }

        public void ExecuteWorkflow(WorkflowInstance instance)
        {
            while (instance.State.ActiveActivities.Any())
            {
                foreach (var activityState in instance.State.ActiveActivities.Where(x => !x.IsExecuting))
                {
                    activityState.CurrentActivity.Execute(instance, activityState);
                }

                instance.TransitionToNextActivity();
            }
        }

        public void CompleteActivity(WorkflowInstance instance, string activityId, Dictionary<string, object> variables)
        {
            instance.CompleteActivity(activityId, variables);
            ExecuteWorkflow(instance);
        }

        public void FailActivity(WorkflowInstance instance, string activityId, Exception exception)
        {
            instance.FailActivity(activityId, exception);
            ExecuteWorkflow(instance);
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
