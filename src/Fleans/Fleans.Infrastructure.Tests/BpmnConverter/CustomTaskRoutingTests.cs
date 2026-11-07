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

    // The parser accepts zeebe:* and fleans:* with identical semantics.
    private static XNamespace Extension(string prefix) =>
        prefix == "zeebe" ? BpmnNamespaces.Zeebe : BpmnNamespaces.Fleans;

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
    [DataRow("zeebe")]
    [DataRow("fleans")]
    public void ResolveTaskType_WithTaskDefinitionExtension_ReturnsType(string prefix)
    {
        var ns = Extension(prefix);
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(ns + "taskDefinition",
                    new XAttribute("type", "rest-call"))));
        Assert.AreEqual("rest-call", _resolver.ResolveTaskType(el));
    }

    private XElement TaskWithVersion(string prefix, string version) =>
        new(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Extension(prefix) + "taskDefinition",
                    new XAttribute("type", "rest-call"),
                    new XAttribute("version", version))));

    [TestMethod]
    [DataRow("zeebe", "1.4.0", "1.4.0")]
    [DataRow("fleans", "1.4.0", "1.4.0")]
    [DataRow("fleans", "1.4.0+build", "1.4.0")]
    [DataRow("fleans", "2.0.0-rc.1", "2.0.0-rc.1")]
    public void ResolveTaskVersion_ReturnsTheNormalizedVersion(string prefix, string version, string expected)
    {
        Assert.AreEqual(expected, _resolver.ResolveTaskVersion(TaskWithVersion(prefix, version), "ct1"));
    }

    [TestMethod]
    public void ResolveTaskVersion_LatestOrAbsent_IsNull()
    {
        var bare = new XElement(Bpmn + "serviceTask", new XAttribute("id", "ct1"), new XAttribute("type", "rest-call"));

        Assert.IsNull(_resolver.ResolveTaskVersion(TaskWithVersion("fleans", "latest"), "ct1"));
        Assert.IsNull(_resolver.ResolveTaskVersion(bare, "ct1"));
    }

    [TestMethod]
    [DataRow("1.4")]
    [DataRow("newest")]
    [DataRow("")]
    public void ResolveTaskVersion_AnythingElse_FailsTheDeployNamingTheTask(string version)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            _resolver.ResolveTaskVersion(TaskWithVersion("fleans", version), "ct1"));

        StringAssert.Contains(ex.Message, "ct1");
    }

    [TestMethod]
    [DataRow("zeebe")]
    [DataRow("fleans")]
    public void ParseMappings_ParsesInputAndOutputLists(string prefix)
    {
        var ns = Extension(prefix);
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(ns + "ioMapping",
                    new XElement(ns + "input",
                        new XAttribute("source", "=userId"),
                        new XAttribute("target", "user_id")),
                    new XElement(ns + "input",
                        new XAttribute("source", "=\"literal\""),
                        new XAttribute("target", "label")),
                    new XElement(ns + "output",
                        new XAttribute("source", "=__response.body.id"),
                        new XAttribute("target", "created_id")))));

        var inputs = IoMappingParser.Parse(el).Inputs;
        var outputs = IoMappingParser.Parse(el).Outputs;

        Assert.AreEqual(2, inputs.Count);
        Assert.AreEqual(1, outputs.Count);
        Assert.AreEqual("user_id", inputs[0].Target);
        Assert.AreEqual("=userId", inputs[0].Source);
        Assert.AreEqual("created_id", outputs[0].Target);
    }

    // ---- Output mapping deploy-time validation (sources are C# expressions, checked at run time) ----

    [TestMethod]
    [DataRow("", "out_id", DisplayName = "Output: empty source")]
    [DataRow("=", "out_id", DisplayName = "Output: bare = with no expression")]
    [DataRow("=valid.path", "1invalid", DisplayName = "Output: target starts with digit")]
    [DataRow("=valid.path", "", DisplayName = "Output: empty target")]
    [DataRow("=valid.path", "with-dash", DisplayName = "Output: hyphen in target")]
    [DataRow("=valid.path", "with space", DisplayName = "Output: space in target")]
    public void DeployTime_RejectsMalformedOutputMapping(string source, string target)
    {
        var el = new XElement(Bpmn + "serviceTask",
            new XAttribute("id", "ct1"),
            new XElement(Bpmn + "extensionElements",
                new XElement(Zeebe + "ioMapping",
                    new XElement(Zeebe + "output",
                        new XAttribute("source", source),
                        new XAttribute("target", target)))));
        Assert.ThrowsExactly<InvalidOperationException>(() => IoMappingParser.Parse(el).Outputs);
    }

    // ---- Input mapping deploy-time validation ----

    [TestMethod]
    [DataRow("", "out_id", DisplayName = "Input: empty source")]
    [DataRow("=", "out_id", DisplayName = "Input: bare = with no expression")]
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
        Assert.ThrowsExactly<InvalidOperationException>(() => IoMappingParser.Parse(el).Inputs);
    }
}
