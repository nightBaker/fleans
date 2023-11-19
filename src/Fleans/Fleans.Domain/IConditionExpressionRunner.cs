namespace Fleans.Domain;

public interface IConditionExpressionRunner
{
    bool Evaluate(IContext context);

    bool Evaluate(IContext context, Exception exception);    
}