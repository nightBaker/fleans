namespace Fleans.Domain.Activities
{
    public record ExclusiveGatewayActivity : Activity<bool>, IExecutableActivity<bool>
    {
        public ExclusiveGatewayActivity(Guid id, IConditionExpressionRunner condition)
            : base(id)
        {
            Condition = condition;
        }

        public IConditionExpressionRunner Condition { get; }
       
        public async Task<IActivityExecutionResult> ExecuteAsync(IContext context)
        {       
            Status = ActivityStatus.Running;
            ExecutionResult = new (Condition.Evaluate(context));
            Status = ActivityStatus.Completed;
            return new ActivityExecutionResult(ActivityResultStatus.Completed);
        }        
    }
}