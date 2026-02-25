using System.Dynamic;
using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class CallActivityVariableMappingTests
{
    [TestMethod]
    public void BuildChildInputVariables_PropagateAll_CopiesAllParentVariables()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub", [], [], PropagateAllParentVariables: true);
        var parent = new ExpandoObject();
        ((IDictionary<string, object?>)parent)["x"] = 1;
        ((IDictionary<string, object?>)parent)["y"] = "hello";

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(2, dict.Count);
        Assert.AreEqual(1, dict["x"]);
        Assert.AreEqual("hello", dict["y"]);
    }

    [TestMethod]
    public void BuildChildInputVariables_NoPropagation_OnlyMappedVariables()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub",
            [new VariableMapping("x", "mapped_x")], [],
            PropagateAllParentVariables: false);
        var parent = new ExpandoObject();
        ((IDictionary<string, object?>)parent)["x"] = 42;
        ((IDictionary<string, object?>)parent)["y"] = "skipped";

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(1, dict.Count);
        Assert.AreEqual(42, dict["mapped_x"]);
    }

    [TestMethod]
    public void BuildChildInputVariables_MappingsOverridePropagated()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub",
            [new VariableMapping("x", "x")], [],
            PropagateAllParentVariables: true);
        var parent = new ExpandoObject();
        ((IDictionary<string, object?>)parent)["x"] = "original";

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert — mapping runs after propagation, same key, same value
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(1, dict.Count);
        Assert.AreEqual("original", dict["x"]);
    }

    [TestMethod]
    public void BuildChildInputVariables_MissingSourceMapping_Skipped()
    {
        // Arrange
        var callActivity = new CallActivity("call1", "sub",
            [new VariableMapping("missing", "target")], [],
            PropagateAllParentVariables: false);
        var parent = new ExpandoObject();

        // Act
        var result = callActivity.BuildChildInputVariables(parent);

        // Assert
        var dict = (IDictionary<string, object?>)result;
        Assert.AreEqual(0, dict.Count);
    }
}
