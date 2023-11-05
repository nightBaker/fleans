using Fleans.Domain.Exceptions;

namespace Fleans.Domain
{
    public record ExclusiveGatewayActivity : Activity<bool>, IActivity<bool>
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
            Condition.Evaluate(context);
            Status = ActivityStatus.Completed;
            return new ActivityExecutionResult(ActivityResultStatus.Completed);
        }        
    }
}