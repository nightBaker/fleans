using Fleans.Domain.Activities;
using Fleans.Domain.Exceptions;

namespace Fleans.Domain
{
    public class WorkflowDefinitionBuilder
    {
        private IActivityBuilder? _firstActivityBuilder;

        private readonly Guid _id;
        private readonly int _version;

        public WorkflowDefinitionBuilder(Guid id, int version)
        {
            _id = id;
            _version = version;
        }
        
        public WorkflowDefinitionBuilder StartWith(IActivityBuilder activityBuilder)
        {
            _firstActivityBuilder = activityBuilder;

            return this;
        }

        public WorkflowDefinition Build()
        {
            if (_firstActivityBuilder is null) throw new FirstActivityNotSpecifiedException();

            var firstActivity = _firstActivityBuilder.Build();

            var allActivities = new List<IActivity> { firstActivity.Activity };
            allActivities.AddRange(firstActivity.ChildActivities);

            var connections = firstActivity.Connections.GroupBy(x => x.From.Id).ToDictionary(x => x.Key, x => x.ToArray());

          //  return new WorkflowDefinition(_id, _version, allActivities.ToArray(), connections);

          throw new NotImplementedException("Reimagination required");
        }
    }
}
