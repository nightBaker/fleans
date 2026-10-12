using System.Dynamic;
using System.Text.Json;

namespace Fleans.Plugins.CamundaExternalTask.Tests;

[TestClass]
public class CamundaVariablesTests
{
    [TestMethod]
    [DataRow(42L, "Integer")]
    [DataRow(5_000_000_000L, "Long")]
    [DataRow(1.5, "Double")]
    [DataRow("x", "String")]
    [DataRow(true, "Boolean")]
    [DataRow(null, "Null")]
    public void ToCamunda_picks_the_camunda_type(object? value, string expectedType) =>
        Assert.AreEqual(expectedType, CamundaVariables.ToCamunda(value).Type);

    [TestMethod]
    public void ToCamunda_serializes_objects_and_lists_as_json()
    {
        // Arrange
        var order = new ExpandoObject();
        ((IDictionary<string, object?>)order)["lines"] = new List<object?> { 1L, "two" };

        // Act
        var typed = CamundaVariables.ToCamunda(order);

        // Assert
        Assert.AreEqual("Json", typed.Type);
        Assert.AreEqual("{\"lines\":[1,\"two\"]}", typed.Value);
    }

    [TestMethod]
    public void ToFleans_infers_untyped_values_from_json()
    {
        // Arrange
        var element = JsonSerializer.SerializeToElement(new { a = 1, b = new[] { true } });

        // Act
        var value = (IDictionary<string, object?>)CamundaVariables.ToFleans("v", new VariableValueDto(element, null, null))!;

        // Assert
        Assert.AreEqual(1L, value["a"]);
        CollectionAssert.AreEqual(new List<object?> { true }, (List<object?>)value["b"]!);
    }

    [TestMethod]
    public void ToFleans_rejects_java_serialized_objects()
    {
        var typed = new VariableValueDto(JsonSerializer.SerializeToElement("rO0AB"), "Object",
            new Dictionary<string, object?> { ["serializationDataFormat"] = "application/x-java-serialized-object" });

        Assert.ThrowsExactly<CamundaVariables.UnsupportedVariableException>(() => CamundaVariables.ToFleans("v", typed));
    }

    [TestMethod]
    public void FormatDate_uses_the_camunda_rest_format() =>
        Assert.AreEqual("2026-03-04T05:06:07.089+0000",
            CamundaVariables.FormatDate(new DateTime(2026, 3, 4, 5, 6, 7, 89, DateTimeKind.Utc)));
}
