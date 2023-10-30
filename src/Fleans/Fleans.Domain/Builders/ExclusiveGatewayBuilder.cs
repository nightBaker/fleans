using Fleans.Domain.Exceptions;

namespace Fleans.Domain;

public class ExclusiveGatewayBuilder : ActivityBuilder<bool>
{
    private IConditionBuilder? _condition;

    public ExclusiveGatewayBuilder Condition(IConditionBuilder conditionBuilder)
    {
        _condition = conditionBuilder;
        return this;
    }

    public ExclusiveGatewayBuilder Then(IActivityBuilder builder, Guid activityId)
    {
        AddConnection(new ExclusiveGatewayCondition(builder.WithId(activityId).Build(), true));

        return this;
    }
    
    public ExclusiveGatewayBuilder Else(IActivityBuilder builder, Guid activityId)
    {
        AddConnection(new ExclusiveGatewayCondition(builder.WithId(activityId).Build(), false));

        return this;
    }
    public override IActivity Build()
    {
        if (_condition is null) throw new ConditionNotSpecifiedException();
        
        var exclusiveGatewayActivity = new GatewayExclusiveActivity(Id, _connections.ToArray(), _condition.Build());
        foreach (var connection in _connections)
        {
            connection.From = exclusiveGatewayActivity;
        }

        return exclusiveGatewayActivity;
    }
}