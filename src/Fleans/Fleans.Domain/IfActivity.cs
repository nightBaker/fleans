using Fleans.Domain.Exceptions;

namespace Fleans.Domain
{
    public record IfActivity : Activity<bool>, IActivity
    {
        public IfActivity(Guid id, IWorkflowConnection[] connections, IConditionExpressionRunner condition) 
            : base(id, connections)
        {
            Condition = condition;
        }

        public IConditionExpressionRunner Condition { get; }

        public Task ExecuteAsync(IContext context)
        {
            AddResult(new ActivityResult<bool>(Condition.Evaluate(context)));

            return Task.CompletedTask;
        }

        public IActivity[] GetNextActivites(IContext context)
        {
            if (IsCompleted)
            {
                return Connections
                    .Where(c => c.From == this && c.CanExecute(context))
                    .Select(c => c.To)
                    .ToArray();
            }

            throw new ActivityNotCompletedException();
        }
    }
}