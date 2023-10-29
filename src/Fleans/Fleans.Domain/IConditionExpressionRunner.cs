namespace Fleans.Domain;

public interface IConditionExpressionRunner
{
    bool Evaluate(IContext context);
}