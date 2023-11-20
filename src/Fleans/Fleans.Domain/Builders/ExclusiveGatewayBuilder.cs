using Fleans.Domain.Connections;
using Fleans.Domain.Exceptions;

namespace Fleans.Domain;

public class ExclusiveGatewayBuilder : ActivityBuilder<bool>
{
    private IConditionBuilder? _condition;
    private IActivityBuilder? _then;
    private IActivityBuilder? _else;

    public ExclusiveGatewayBuilder(Guid id) : base(id)
    {
    }

    public ExclusiveGatewayBuilder Condition(IConditionBuilder conditionBuilder)
    {
        _condition = conditionBuilder;
        return this;
    }

    public ExclusiveGatewayBuilder Then(IActivityBuilder builder)
    {
        _then = builder;

        return this;
    }

    public ExclusiveGatewayBuilder Else(IActivityBuilder builder)
    {
        _else = builder;

        return this;
    }
    public override ActivityBuilderResult Build()
    {
        if (_condition is null) throw new ConditionNotSpecifiedException();
        if (_then is null) throw new ThenBranchNotSpecifiedException();

        var exclusiveGatewayActivity = new ExclusiveGatewayActivity(Id, _condition.Build());
        
        var thenActivity = _then.Build();
        var elseActivity = _else?.Build();

        var childActivities = new List<IActivity> { thenActivity.Activity };

        childActivities.AddRange(thenActivity.ChildActivities);

        var connections = new List<IWorkflowConnection<IActivity, IActivity>>
        {
            new ExclusiveGatewayConnection(exclusiveGatewayActivity, thenActivity.Activity, true),            
        };

        connections.AddRange(thenActivity.Connections);

        if (elseActivity is not null)
        {
            childActivities.Add(elseActivity.Activity);
            childActivities.AddRange(elseActivity.ChildActivities);
            connections.Add(new ExclusiveGatewayConnection(exclusiveGatewayActivity, elseActivity.Activity, false));
            connections.AddRange(elseActivity.Connections);
        }

        return new ActivityBuilderResult
        {
            ChildActivities = childActivities,
            Activity = exclusiveGatewayActivity,
            Connections = connections
        };
    }
}
