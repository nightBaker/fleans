using Fleans.Application.CustomTasks;
using Fleans.Domain.Activities;
using Newtonsoft.Json.Linq;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class CustomTaskInputRulesTests
{
    private static readonly CustomTaskParameterSchema Schema = new(
    [
        new("url", null, CustomTaskParameterType.String, Required: true, null, null),
        new("timeoutSec", null, CustomTaskParameterType.Integer, Required: false, null, "30"),
        new("verbose", null, CustomTaskParameterType.Boolean, Required: false, null, null),
        new("when", null, CustomTaskParameterType.Expression, Required: false, null, null),
        new("codes", null, CustomTaskParameterType.List, Required: false, null, null, CustomTaskParameterType.Integer),
        new("headers", null, CustomTaskParameterType.Map, Required: false, null, null, CustomTaskParameterType.String),
    ]);

    private static List<string> Sources(params (string Source, string Target)[] inputs) =>
        CustomTaskInputRules.CheckSources(Schema,
            [new InputMapping("https://x", "url"), .. inputs.Select(i => new InputMapping(i.Source, i.Target))]);

    private static List<string> Values(string json)
    {
        var inputs = JObject.Parse(json);
        inputs["url"] ??= "https://x";
        return CustomTaskInputRules.CheckValues(Schema, inputs);
    }

    [TestMethod]
    [DataRow("30", "timeoutSec")]
    [DataRow("-1", "timeoutSec")]
    [DataRow("=anything", "timeoutSec")]
    [DataRow("", "timeoutSec")]
    [DataRow("true", "verbose")]
    [DataRow("FALSE", "verbose")]
    [DataRow("=anything", "verbose")]
    [DataRow("=anything", "codes")]
    [DataRow("=anything", "headers")]
    [DataRow("free text", "when")]
    public void Deploy_accepts_sources_that_fit(string source, string target)
    {
        Assert.IsEmpty(Sources((source, target)));
    }

    [TestMethod]
    [DataRow("abc", "timeoutSec", "whole number")]
    [DataRow("1.5", "timeoutSec", "whole number")]
    [DataRow("maybe", "verbose", "true or false")]
    [DataRow("200", "codes", "literal")]
    [DataRow("Accept", "headers", "literal")]
    [DataRow("=expr", "timeoutSecs", "not a parameter")]
    public void Deploy_names_a_source_that_does_not_fit(string source, string target, string problem)
    {
        // Act
        var violation = Sources((source, target)).Single();

        // Assert
        StringAssert.Contains(violation, target);
        StringAssert.Contains(violation, problem);
    }

    [TestMethod]
    public void Deploy_reports_a_missing_required_parameter_even_with_a_default()
    {
        // Arrange
        var schema = new CustomTaskParameterSchema([new("timeoutSec", null, CustomTaskParameterType.Integer, Required: true, null, "30")]);

        // Act + Assert
        StringAssert.Contains(CustomTaskInputRules.CheckSources(schema, []).Single(), "required parameter 'timeoutSec'");
        Assert.IsEmpty(CustomTaskInputRules.CheckSources(schema, [new InputMapping("=t", "timeoutSec")]));
    }

    [TestMethod]
    public void A_blank_required_number_counts_as_missing()
    {
        // Arrange
        var schema = new CustomTaskParameterSchema([new("timeoutSec", null, CustomTaskParameterType.Integer, Required: true, null, null)]);

        // Act + Assert
        StringAssert.Contains(CustomTaskInputRules.CheckSources(schema, [new InputMapping(" ", "timeoutSec")]).Single(), "required");
        StringAssert.Contains(CustomTaskInputRules.CheckValues(schema, JObject.Parse("""{"timeoutSec":" "}""")).Single(), "required");
    }

    [TestMethod]
    [DataRow("""{"timeoutSec":30}""")]
    [DataRow("""{"timeoutSec":30.0}""")]
    [DataRow("""{"timeoutSec":"30"}""")]
    [DataRow("""{"timeoutSec":""}""")]
    [DataRow("""{"timeoutSec":null}""")]
    [DataRow("""{"verbose":true}""")]
    [DataRow("""{"verbose":"True"}""")]
    [DataRow("""{"url":123}""")]
    [DataRow("""{"when":{"any":["thing"]}}""")]
    [DataRow("""{"codes":[200,"201",null]}""")]
    [DataRow("""{"headers":{"a":"b","n":1,"none":null}}""")]
    public void Dispatch_accepts_values_that_fit(string json)
    {
        Assert.IsEmpty(Values(json));
    }

    [TestMethod]
    [DataRow("""{"timeoutSec":1.5}""", "whole number")]
    [DataRow("""{"timeoutSec":"abc"}""", "whole number")]
    [DataRow("""{"timeoutSec":true}""", "whole number")]
    [DataRow("""{"verbose":1}""", "true or false")]
    [DataRow("""{"url":{"a":1}}""", "text")]
    [DataRow("""{"url":[1]}""", "text")]
    [DataRow("""{"codes":200}""", "a list")]
    [DataRow("""{"codes":["x"]}""", "whole number")]
    [DataRow("""{"headers":"Accept"}""", "a map")]
    [DataRow("""{"headers":{"a":{"nested":1}}}""", "text")]
    [DataRow("""{"timeoutSecs":30}""", "not a parameter")]
    public void Dispatch_names_a_value_that_does_not_fit(string json, string problem)
    {
        StringAssert.Contains(Values(json).Single(), problem);
    }

    [TestMethod]
    public void Dispatch_does_not_repeat_the_value_it_rejects()
    {
        // Act — an input's value can be a secret, and this text is kept as the activity's error
        var violation = Values("""{"timeoutSec":"s3cret"}""").Single();

        // Assert
        Assert.DoesNotContain("s3cret", violation);
    }

    [TestMethod]
    public void Dispatch_reports_a_missing_required_parameter()
    {
        StringAssert.Contains(CustomTaskInputRules.CheckValues(Schema, new JObject()).Single(), "required parameter 'url'");
        StringAssert.Contains(CustomTaskInputRules.CheckValues(Schema, JObject.Parse("""{"url":null}""")).Single(), "required parameter 'url'");
    }

    [TestMethod]
    public void Schemas_with_the_same_parameters_in_another_order_share_a_contract()
    {
        // Arrange
        CustomTaskParameterSpec a = new("a", "Label", CustomTaskParameterType.String, true, "text", "x");
        CustomTaskParameterSpec b = new("b", null, CustomTaskParameterType.Integer, false, null, null);

        // Act + Assert
        Assert.IsTrue(CustomTaskParameterSchema.HaveSameContract(new([a, b]), new([b, a with { DisplayName = "Other", DefaultValue = "y" }])));
        Assert.IsTrue(CustomTaskParameterSchema.HaveSameContract(null, null));
        Assert.IsFalse(CustomTaskParameterSchema.HaveSameContract(new([a]), null));
        Assert.IsFalse(CustomTaskParameterSchema.HaveSameContract(new([a]), new([a, b])));
        Assert.IsFalse(CustomTaskParameterSchema.HaveSameContract(new([a]), new([a with { Required = false }])));
        Assert.IsFalse(CustomTaskParameterSchema.HaveSameContract(new([b]), new([b with { Type = CustomTaskParameterType.String }])));
    }
}
