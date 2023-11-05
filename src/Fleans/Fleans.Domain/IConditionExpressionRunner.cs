namespace Fleans.Domain;

public interface IConditionExpressionRunner
{
    void Evaluate(IContext context);

    bool Result { get; }
}