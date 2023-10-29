namespace Fleans.Domain;

public interface IConditionBuilder
{
    IConditionExpressionRunner Build();
}