using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Domain
{
    public class WorkflowBuilder
    {        
        private Dictionary<string, object> _initialContext = new();
        private WorkflowDefinitionBuilder? _workflowDefinitionBuilder;

        public WorkflowBuilder With(Dictionary<string, object> initialContext)
        {
            _initialContext = initialContext;
            
            return this;
        }

        public WorkflowBuilder With(WorkflowDefinitionBuilder workflowDefinitionBuilder)
        {
            _workflowDefinitionBuilder = workflowDefinitionBuilder;

            return this;
        }

        public Workflow Build(Guid id)
        {
            if(_workflowDefinitionBuilder is null) throw new ArgumentException("Workflow definition builder is not specified");
            
            var workflowDefinition = _workflowDefinitionBuilder.Build();

            return new Workflow(id, _initialContext, workflowDefinition.Activities.First(), workflowDefinition );
        }
    }
}
