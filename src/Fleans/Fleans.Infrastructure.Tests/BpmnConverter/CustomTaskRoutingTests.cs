using Fleans.Domain.Activities;
using Fleans.Infrastructure.Bpmn;
using System.Text;
using System.Xml.Linq;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class CustomTaskRoutingTests : BpmnConverterTestBase
{
    private static readonly XNamespace Bpmn   = BpmnNamespaces.Bpmn;
    private static readonly XNamespace Zeebe  = BpmnNamespaces.Zeebe;
    private static readonly XNamespace Fleans = BpmnNamespaces.Fleans;

    private CustomTaskRoutingResolver _resolver = null!;

    [TestInitialize]
    public void SetupResolver()
    {
        _resolver = new CustomTaskRoutingResolver();
    }

    // Integration: verifies BpmnConverter creates TaskActivity when resolver returns null
    [TestMethod]
    public async Task ServiceTask_WithoutTypeAttribute_ParsesAsTaskActivity()
    {
        var bpmn = CreateBpmnWithServiceTask("wf", "st1");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));
        Assert.IsNotNull(workflow.Activities.OfType<TaskActivity>().FirstOrDefault(a => a.ActivityId == "st1"));
        Assert.IsNull(workflow.Activities.OfType<CustomTaskActivity>().FirstOrDefault());
    }

    [TestMethod]
    public void ResolveTaskType_WithTypeAttribute_ReturnsType()
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XAttribute("type", "rest-call"));
        Assert.AreEqual("rest-call", _resolver.ResolveTaskType(el));
    }

    [TestMethod]
    public void ResolveTaskType_WithoutTypeAttribute_ReturnsNull()
    {
        var el = new XElement(Bpmn + "serviceTask", new XAttribute("id", "ct1"));
        Assert.IsNull(_resolver.ResolveTaskType(el));
    }

    [TestMethod]
    public void ResolveTaskType_WithZeebeTaskDefinition_ReturnsType()
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Zeebe + "taskDefinition",
                    new XAttribute("type", "rest-call"))));
        Assert.AreEqual("rest-call", _resolver.ResolveTaskType(el));
    }

    [TestMethod]
    public void ParseMappings_ParsesInputAndOutputLists()
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Zeebe + "ioMapping",
                    new XElement(Zeebe + "input",
                        new XAttribute("source", "=userId"),
                        new XAttribute("target", "user_id")),
                    new XElement(Zeebe + "input",
                        new XAttribute("source", "=\"literal\""),
                        new XAttribute("target", "label")),
                    new XElement(Zeebe + "output",
                        new XAttribute("source", "=__response.body.id"),
                        new XAttribute("target", "created_id")))));

        var inputs = _resolver.ParseInputMappings(el);
        var outputs = _resolver.ParseOutputMappings(el);

        Assert.AreEqual(2, inputs.Count);
        Assert.AreEqual(1, outputs.Count);
        Assert.AreEqual("user_id", inputs[0].Target);
        Assert.AreEqual("=userId", inputs[0].Source);
        Assert.AreEqual("created_id", outputs[0].Target);
    }

    // ---- Output mapping deploy-time validation (9 scenarios) ----

    [TestMethod]
    [DataRow("", "out_id", DisplayName = "Output: empty source")]
    [DataRow("=", "out_id", DisplayName = "Output: bare = with no expression")]
    [DataRow("=\"unmatched", "out_id", DisplayName = "Output: unmatched quote")]
    [DataRow("=path.with-dash.x", "out_id", DisplayName = "Output: invalid path segment (hyphen)")]
    [DataRow("=valid.path", "1invalid", DisplayName = "Output: target starts with digit")]
    [DataRow("=valid.path", "", DisplayName = "Output: empty target")]
    [DataRow("=valid.path", "with-dash", DisplayName = "Output: hyphen in target")]
    [DataRow("=valid.path", "with space", DisplayName = "Output: space in target")]
    [DataRow("=valid.path", "__response", DisplayName = "Output: __response is reserved")]
    public void DeployTime_RejectsMalformedOutputMapping(string source, string target)
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Zeebe + "ioMapping",
                    new XElement(Zeebe + "output",
                        new XAttribute("source", source),
                        new XAttribute("target", target)))));
        Assert.ThrowsExactly<InvalidOperationException>(() => _resolver.ParseOutputMappings(el));
    }

    // ---- Input mapping deploy-time validation (8 scenarios) ----

    [TestMethod]
    [DataRow("", "out_id", DisplayName = "Input: empty source")]
    [DataRow("=", "out_id", DisplayName = "Input: bare = with no expression")]
    [DataRow("=\"unmatched", "out_id", DisplayName = "Input: unmatched quote")]
    [DataRow("=path.with-dash.x", "out_id", DisplayName = "Input: invalid path segment (hyphen)")]
    [DataRow("=valid.path", "1invalid", DisplayName = "Input: target starts with digit")]
    [DataRow("=valid.path", "", DisplayName = "Input: empty target")]
    [DataRow("=valid.path", "with-dash", DisplayName = "Input: hyphen in target")]
    [DataRow("=valid.path", "with space", DisplayName = "Input: space in target")]
    public void DeployTime_RejectsMalformedInputMapping(string source, string target)
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Zeebe + "ioMapping",
                    new XElement(Zeebe + "input",
                        new XAttribute("source", source),
                        new XAttribute("target", target)))));
        Assert.ThrowsExactly<InvalidOperationException>(() => _resolver.ParseInputMappings(el));
    }

    // ---- fleans:* companion cases (parser must accept either prefix with identical semantics) ----

    [TestMethod]
    public void ResolveTaskType_WithFleansTaskDefinition_ReturnsType()
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Fleans + "taskDefinition",
                    new XAttribute("type", "rest-call"))));
        Assert.AreEqual("rest-call", _resolver.ResolveTaskType(el));
    }

    [TestMethod]
    public void ParseMappings_WithFleansNamespace_ParsesInputAndOutputLists()
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Fleans + "ioMapping",
                    new XElement(Fleans + "input",
                        new XAttribute("source", "=userId"),
                        new XAttribute("target", "user_id")),
                    new XElement(Fleans + "input",
                        new XAttribute("source", "=\"literal\""),
                        new XAttribute("target", "label")),
                    new XElement(Fleans + "output",
                        new XAttribute("source", "=__response.body.id"),
                        new XAttribute("target", "created_id")))));

        var inputs = _resolver.ParseInputMappings(el);
        var outputs = _resolver.ParseOutputMappings(el);

        Assert.AreEqual(2, inputs.Count);
        Assert.AreEqual(1, outputs.Count);
        Assert.AreEqual("user_id", inputs[0].Target);
        Assert.AreEqual("=userId", inputs[0].Source);
        Assert.AreEqual("created_id", outputs[0].Target);
    }
}
