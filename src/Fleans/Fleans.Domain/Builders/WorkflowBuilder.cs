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
        private readonly List<IWorkflowConnection> _sequences = new List<IWorkflowConnection>();
        private Dictionary<string, object> _initialContext = new Dictionary<string, object>();
        private IActivityBuilder? _firstActivityBuilder;

        public WorkflowBuilder StartWith(Dictionary<string, object> initialContext)
        {
            _initialContext = initialContext;

            return this;
        }

        private WorkflowBuilder AddFirstActivity(IActivityBuilder activityBuilder)
        {
            _firstActivityBuilder = activityBuilder;

            return this;
        }

        public Workflow Build(Guid id, int version)
        {
            if (_firstActivityBuilder is null) throw new ArgumentNullException("First activity is not specified");
            return new Workflow(id, version, _initialContext, _firstActivityBuilder.Build(id));
            // TODO activity id 
        }
    }
}
