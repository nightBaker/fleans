using Fleans.Domain.Exceptions;

namespace Fleans.Domain.Builders
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
            if(_workflowDefinitionBuilder is null) throw new WorkflowВefinitionNotSpecifiedException();
            
            var workflowDefinition = _workflowDefinitionBuilder.Build();

            return new Workflow(id, _initialContext, workflowDefinition );
        }
    }
}
