using System.Dynamic;
using System.Text.Json;
using Fleans.Application.ExternalTasks;
using Newtonsoft.Json.Linq;

namespace Fleans.Application.Tests.ExternalTasks;

[TestClass]
public class ExternalTaskJsonTests
{
    [TestMethod]
    public void Serialize_WritesJsonElementValuesAsTheirJson()
    {
        // Arrange — the Redis stream provider hands event payloads back as JsonElements
        var inputs = (IDictionary<string, object?>)new ExpandoObject();
        inputs["delayMs"] = JsonSerializer.SerializeToElement(10_000);
        inputs["order"] = JsonSerializer.SerializeToElement(new { id = "o-1", lines = new[] { 1, 2 } });
        inputs["plain"] = "text";

        // Act
        var json = JObject.Parse(ExternalTaskJson.Serialize(inputs));

        // Assert
        Assert.AreEqual(10_000L, json["delayMs"]!.Value<long>());
        Assert.AreEqual("o-1", json["order"]!["id"]!.Value<string>());
        Assert.HasCount(2, (JArray)json["order"]!["lines"]!);
        Assert.AreEqual("text", json["plain"]!.Value<string>());
    }
}
