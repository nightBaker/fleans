namespace Fleans.Domain.Builders;

public interface IConditionBuilder
{
    IConditionExpressionRunner Build();
}