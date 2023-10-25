namespace Fleans.Domain
{
    public record IfActivity : Activity<bool>, IActivity
    {
        public IfActivity(IConditionExpressionRunner condition)
        {
            Condition = condition;
        }

        public IConditionExpressionRunner Condition { get; }

        public Task ExecuteAsync(IContext context)
        {
            AddResult(new ActivityResult<bool>(Condition.Evaluate(context)));

            return Task.CompletedTask;
        }
    }
}