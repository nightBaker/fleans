using Fleans.Application.Conditions;
using Fleans.Application.Scripts;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;

namespace Fleans.Application.Deployments;

/// <summary>
/// Compiles every condition and script of a definition with the evaluators instances use, so a
/// syntax error rejects the deploy instead of failing the first instances that reach it (#975).
/// Compiling also fills those evaluators' caches on this silo.
/// </summary>
public sealed class WorkflowExpressionValidator
{
    private readonly IConditionExpressionEvaluator _conditions;
    private readonly IScriptExpressionExecutor _scripts;

    public WorkflowExpressionValidator(IConditionExpressionEvaluator conditions, IScriptExpressionExecutor scripts)
    {
        _conditions = conditions;
        _scripts = scripts;
    }

    /// <summary>Throws an <see cref="ArgumentException"/> naming each element whose expression does not compile.</summary>
    public void Validate(IWorkflowDefinition workflow)
    {
        var errors = new List<string>();
        CheckScope(workflow, errors);
        if (errors.Count > 0)
            throw new ArgumentException(
                $"Process '{workflow.WorkflowId}' has invalid expressions: {string.Join("; ", errors)}");
    }

    private void CheckScope(IWorkflowDefinition scope, List<string> errors)
    {
        foreach (var flow in scope.SequenceFlows.OfType<ConditionalSequenceFlow>())
            Check(errors, $"sequence flow '{flow.SequenceFlowId}' condition", () => _conditions.Compile(flow.Condition));

        foreach (var activity in scope.Activities)
            CheckActivity(activity, errors);
    }

    private void CheckActivity(Activity activity, List<string> errors)
    {
        var id = activity.ActivityId;
        switch (activity)
        {
            case ScriptTask script:
                Check(errors, $"script task '{id}'", () => _scripts.Compile(script.Script, script.ScriptFormat));
                break;
            case ComplexGateway { ActivationCondition: { } activation }:
                Check(errors, $"complex gateway '{id}' activation condition", () => _conditions.Compile(activation));
                break;
            case ConditionalStartEvent start:
                Check(errors, $"conditional start event '{id}'", () => _conditions.Compile(start.ConditionExpression));
                break;
            case ConditionalIntermediateCatchEvent catchEvent:
                Check(errors, $"conditional catch event '{id}'", () => _conditions.Compile(catchEvent.ConditionExpression));
                break;
            case ConditionalBoundaryEvent boundary:
                Check(errors, $"conditional boundary event '{id}'", () => _conditions.Compile(boundary.ConditionExpression));
                break;
            case MultiInstanceActivity multiInstance:
                if (multiInstance.CompletionCondition is { } completion)
                    Check(errors, $"multi-instance '{id}' completion condition", () => _conditions.Compile(completion));
                CheckActivity(multiInstance.InnerActivity, errors);
                break;
        }

        // Sub-processes, transactions and event sub-processes carry their own flows and activities.
        if (activity is IWorkflowDefinition nested)
            CheckScope(nested, errors);
    }

    private static void Check(List<string> errors, string element, Action compile)
    {
        try
        {
            compile();
        }
        catch (Exception ex)
        {
            errors.Add($"{element}: {ex.Message}");
        }
    }
}
