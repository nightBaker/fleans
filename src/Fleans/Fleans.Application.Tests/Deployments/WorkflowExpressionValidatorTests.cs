using Fleans.Application.Deployments;
using Fleans.Domain;
using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Infrastructure.Conditions;
using Fleans.Infrastructure.Scripts;

namespace Fleans.Application.Tests.Deployments;

[TestClass]
public class WorkflowExpressionValidatorTests
{
    // The evaluators instances use, so the test pins what a deploy accepts to what runs.
    private readonly WorkflowExpressionValidator _validator = new(
        new DynamicExpressoConditionExpressionEvaluator(), new DynamicExpressoScriptExpressionExecutor());

    [TestMethod]
    public void Validate_ValidExpressions_DoesNotThrow()
    {
        // Arrange
        var workflow = Workflow(
            condition: "_context.amount > 100",
            script: "_context.total = _context.amount * 2",
            activation: "_context._nroftoken >= 2",
            completion: "_context.nrOfCompletedInstances >= 2");

        // Act & Assert
        _validator.Validate(workflow);
    }

    [TestMethod]
    public void Validate_BrokenExpressions_ThrowsNamingEveryElement()
    {
        // Arrange — one broken expression of each kind, the flow condition inside a sub-process
        var workflow = Workflow(
            condition: "_context.amount >",
            script: "_context.total = (",
            activation: "_context._nroftoken >=",
            completion: "nrOfCompletedInstances +");

        // Act
        var ex = Assert.ThrowsExactly<ArgumentException>(() => _validator.Validate(workflow));

        // Assert
        StringAssert.Contains(ex.Message, "sequence flow 'toEnd' condition");
        StringAssert.Contains(ex.Message, "script task 'calc'");
        StringAssert.Contains(ex.Message, "complex gateway 'join' activation condition");
        StringAssert.Contains(ex.Message, "multi-instance 'mi' completion condition");
    }

    [TestMethod]
    public void Validate_BrokenConditionalEvent_Throws()
    {
        // Arrange
        var start = new ConditionalStartEvent("start", "_context.ready ==");
        var end = new EndEvent("end");
        var workflow = new WorkflowDefinition
        {
            WorkflowId = "conditional",
            Activities = [start, end],
            SequenceFlows = [new SequenceFlow("f1", start, end)]
        };

        // Act
        var ex = Assert.ThrowsExactly<ArgumentException>(() => _validator.Validate(workflow));

        // Assert
        StringAssert.Contains(ex.Message, "conditional start event 'start'");
    }

    private static WorkflowDefinition Workflow(string condition, string script, string activation, string completion)
    {
        var subStart = new StartEvent("subStart");
        var calc = new ScriptTask("calc", script);
        var subEnd = new EndEvent("subEnd");
        var sub = new SubProcess("sub")
        {
            Activities = [subStart, calc, subEnd],
            SequenceFlows =
            [
                new SequenceFlow("toCalc", subStart, calc),
                new ConditionalSequenceFlow("toEnd", calc, subEnd, condition)
            ]
        };

        var start = new StartEvent("start");
        var mi = new MultiInstanceActivity("mi", new TaskActivity("mi"), LoopCardinality: 3, CompletionCondition: completion);
        var join = new ComplexGateway("join", IsFork: false, activation);
        var end = new EndEvent("end");
        return new WorkflowDefinition
        {
            WorkflowId = "expressions",
            Activities = [start, sub, mi, join, end],
            SequenceFlows =
            [
                new SequenceFlow("f1", start, sub),
                new SequenceFlow("f2", sub, mi),
                new SequenceFlow("f3", mi, join),
                new SequenceFlow("f4", join, end)
            ]
        };
    }
}
