using System.Dynamic;
using Fleans.Infrastructure.Conditions;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class DynamicExpressoMappingExpressionEvaluatorTests
{
    private DynamicExpressoMappingExpressionEvaluator _evaluator = null!;

    [TestInitialize]
    public void Setup() => _evaluator = new DynamicExpressoMappingExpressionEvaluator();

    private static ExpandoObject Vars(params (string Key, object? Value)[] entries)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in entries)
            ((IDictionary<string, object?>)vars)[key] = value;
        return vars;
    }

    private static ExpandoObject Order(double total, string status)
        => Vars(("Total", total), ("Status", status));

    [TestMethod]
    public void BareSource_IsAStringLiteral()
        => Assert.AreEqual("hello world", _evaluator.Evaluate("hello world", Vars()));

    [TestMethod]
    public void Path_ReadsTopLevelAndNestedVariables()
    {
        var vars = Vars(("userId", "u-1"), ("order", Order(10, "open")));

        Assert.AreEqual("u-1", _evaluator.Evaluate("=userId", vars));
        Assert.AreEqual("open", _evaluator.Evaluate("=order.Status", vars));
    }

    [TestMethod]
    public void Path_WalksPlainDictionaries()
    {
        // A plugin result holds plain dictionaries, which C# member access cannot read.
        var response = new Dictionary<string, object?> { ["body"] = new Dictionary<string, object?> { ["id"] = 7L } };

        Assert.AreEqual(7L, _evaluator.Evaluate("=__response.body.id", Vars(("__response", response))));
    }

    [TestMethod]
    public void Path_MissingSegment_IsNull()
    {
        var vars = Vars(("order", Order(10, "open")));

        Assert.IsNull(_evaluator.Evaluate("=missing", vars));
        Assert.IsNull(_evaluator.Evaluate("=order.Missing.Deeper", vars));
    }

    [TestMethod]
    public void Path_ThroughContext_ReadsTheScope()
        => Assert.AreEqual(5L, _evaluator.Evaluate("=_context.count", Vars(("count", 5L))));

    [TestMethod]
    public void Literals_KeepTheirLegacyTypes()
    {
        Assert.AreEqual(42L, _evaluator.Evaluate("=42", Vars()));
        Assert.AreEqual(1.5, _evaluator.Evaluate("=1.5", Vars()));
        Assert.AreEqual(true, _evaluator.Evaluate("=true", Vars()));
        Assert.IsNull(_evaluator.Evaluate("=null", Vars()));
        Assert.AreEqual("literal", _evaluator.Evaluate("=\"literal\"", Vars()));
    }

    [TestMethod]
    public void Expression_ComputesOverVariables()
    {
        var vars = Vars(("order", Order(100, "open")), ("count", 3L), ("name", "ada"));

        Assert.AreEqual(112.0, (double)_evaluator.Evaluate("=order.Total * 1.12", vars)!, 1e-9);
        Assert.AreEqual(4L, _evaluator.Evaluate("=count + 1", vars));
        Assert.AreEqual("ADA", _evaluator.Evaluate("=name.ToUpper()", vars));
        Assert.AreEqual("big", _evaluator.Evaluate("=count > 2 ? \"big\" : \"small\"", vars));
    }

    [TestMethod]
    public void Expression_ThroughContext_ComputesOverVariables()
        => Assert.AreEqual(true, _evaluator.Evaluate("=_context.count > 2", Vars(("count", 3L))));

    [TestMethod]
    public void Expression_RecompilesWhenAVariableChangesType()
    {
        Assert.AreEqual(4L, _evaluator.Evaluate("=x + 1", Vars(("x", 3L))));
        Assert.AreEqual("a1", _evaluator.Evaluate("=x + 1", Vars(("x", "a"))));
    }

    [TestMethod]
    public void Expression_WithMissingVariable_TreatsItAsNull()
        => Assert.AreEqual(true, _evaluator.Evaluate("=missing == null", Vars()));

    [TestMethod]
    public void InvalidExpression_Throws()
    {
        Assert.Throws<Exception>(() => _evaluator.Evaluate("=count +", Vars(("count", 1L))));
        Assert.Throws<Exception>(() => _evaluator.Evaluate("=order.Total * 2", Vars()));
    }

    [TestMethod]
    public void BareEquals_Throws()
        => Assert.Throws<InvalidOperationException>(() => _evaluator.Evaluate("=  ", Vars()));
}
