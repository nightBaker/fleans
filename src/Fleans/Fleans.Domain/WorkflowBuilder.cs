using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Domain
{
    public class WorkflowBuilder
    {
        private readonly List<IActivity> _activities = new List<IActivity>();
        private readonly List<IWorkflowSequence> _sequences = new List<IWorkflowSequence>();
        private Dictionary<string, object> _initialContext = new Dictionary<string, object>();

        public WorkflowBuilder StartWith(Dictionary<string, object> initialContext)
        {
            _initialContext = initialContext;

            return this;
        }
        
        public WorkflowBuilder AddActivity(IActivity activity)
        {
            _activities.Add(activity);

            return this;
        }

        public WorkflowBuilder AddSequence(IWorkflowSequence sequence)
        {
            _sequences.Add(sequence);

            return this;
        }

        public Workflow Build(Guid id, int version) 
            => new (id, version, _activities, _initialContext, _sequences);
    }

    public static class WorkflowBuilderExtensions
    {
        public void If(this WorkflowBuilder builder, IActivity activity, )
        {
            var sequence = new WorkflowSequence(from, to);

            builder.AddSequence(sequence);

            return builder;
        }
    }
}
