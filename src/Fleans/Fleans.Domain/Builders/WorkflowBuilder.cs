using Fleans.Domain.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Domain
{
    public class WorkflowBuilder
    {
        private readonly List<IActivity> _activities = new();
        private readonly List<IWorkflowConnection> _sequences = new();
        private Dictionary<string, object> _initialContext = new();
        private IActivityBuilder? _firstActivityBuilder;

        public WorkflowBuilder StartWith(Dictionary<string, object> initialContext)
        {
            _initialContext = initialContext;

            return this;
        }

        private WorkflowBuilder SetFirstActivity(IActivityBuilder activityBuilder)
        {
            _firstActivityBuilder = activityBuilder;

            return this;
        }

        public Workflow Build(Guid id, int version)
        {
            if (_firstActivityBuilder is null) throw new FirstActivityNotSpecifiedException();
            return new Workflow(id, version, _initialContext, _firstActivityBuilder.Build(id));
            // TODO activity id 
        }
    }
}
