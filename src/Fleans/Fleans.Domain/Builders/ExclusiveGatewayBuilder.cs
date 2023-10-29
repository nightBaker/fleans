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
        AddConnection(new ExclusiveGatewayCondition(builder.Build(activityId), true));

        return this;
    }
    
    public ExclusiveGatewayBuilder Else(IActivityBuilder builder, Guid activityId)
    {
        AddConnection(new ExclusiveGatewayCondition(builder.Build(activityId), false));

        return this;
    }
    public override IActivity Build(Guid id)
    {
        if (_condition is null) throw new ArgumentNullException("Condition is not specified");
        
        var exclusiveGatewayActivity = new GatewayExclusiveActivity(id, _connections.ToArray(), _condition.Build());
        foreach (var connection in _connections)
        {
            connection.From = exclusiveGatewayActivity;
        }

        return exclusiveGatewayActivity;
    }
}