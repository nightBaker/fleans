using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class CustomTaskRoutingTests : BpmnConverterTestBase
{
    private const string ZeebeNs = "xmlns:zeebe=\"http://camunda.org/schema/zeebe/1.0\"";
    private const string BpmnNs = "xmlns=\"http://www.omg.org/spec/BPMN/20100524/MODEL\"";

    private static string CreateBpmnWithCustomServiceTask(string taskType, string ioMappingXml = "")
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions {BpmnNs} {ZeebeNs}>
  <process id=""wf"">
    <startEvent id=""start"" />
    <serviceTask id=""ct1"" type=""{taskType}"">
      <extensionElements>
        <zeebe:ioMapping>{ioMappingXml}</zeebe:ioMapping>
      </extensionElements>
    </serviceTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ct1"" />
    <sequenceFlow id=""f2"" sourceRef=""ct1"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private async Task<CustomTaskActivity> ParseSingleCustomTask(string taskType, string ioMappingXml)
    {
        var bpmn = CreateBpmnWithCustomServiceTask(taskType, ioMappingXml);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));
        return workflow.Activities.OfType<CustomTaskActivity>().Single();
    }

    [TestMethod]
    public async Task ServiceTask_WithoutTypeAttribute_ParsesAsTaskActivity()
    {
        var bpmn = CreateBpmnWithServiceTask("wf", "st1");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));
        Assert.IsNotNull(workflow.Activities.OfType<TaskActivity>().FirstOrDefault(a => a.ActivityId == "st1"));
        Assert.IsNull(workflow.Activities.OfType<CustomTaskActivity>().FirstOrDefault());
    }

    [TestMethod]
    public async Task ServiceTask_WithTypeAttribute_ParsesAsCustomTaskActivity()
    {
        var ct = await ParseSingleCustomTask("rest-call", "");
        Assert.AreEqual("rest-call", ct.TaskType);
        Assert.AreEqual(0, ct.InputMappings.Count);
        Assert.AreEqual(0, ct.OutputMappings.Count);
    }

    [TestMethod]
    public async Task ServiceTask_WithZeebeTaskDefinition_ParsesAsCustomTaskActivity()
    {
        var bpmn = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions {BpmnNs} {ZeebeNs}>
  <process id=""wf"">
    <startEvent id=""start"" />
    <serviceTask id=""ct1"">
      <extensionElements>
        <zeebe:taskDefinition type=""rest-call"" />
      </extensionElements>
    </serviceTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""ct1"" />
    <sequenceFlow id=""f2"" sourceRef=""ct1"" targetRef=""end"" />
  </process>
</definitions>";
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));
        var ct = workflow.Activities.OfType<CustomTaskActivity>().Single();
        Assert.AreEqual("rest-call", ct.TaskType);
    }

    [TestMethod]
    public async Task ServiceTask_WithInputAndOutputMappings_ParsesBothLists()
    {
        var io = @"
          <zeebe:input source=""=userId"" target=""user_id"" />
          <zeebe:input source=""=&quot;literal&quot;"" target=""label"" />
          <zeebe:output source=""=__response.body.id"" target=""created_id"" />";
        var ct = await ParseSingleCustomTask("rest-call", io);
        Assert.AreEqual(2, ct.InputMappings.Count);
        Assert.AreEqual(1, ct.OutputMappings.Count);
        Assert.AreEqual("user_id", ct.InputMappings[0].Target);
        Assert.AreEqual("=userId", ct.InputMappings[0].Source);
        Assert.AreEqual("created_id", ct.OutputMappings[0].Target);
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
    public async Task DeployTime_RejectsMalformedOutputMapping(string source, string target)
    {
        var io = $@"<zeebe:output source=""{System.Security.SecurityElement.Escape(source)}"" target=""{System.Security.SecurityElement.Escape(target)}"" />";
        var bpmn = CreateBpmnWithCustomServiceTask("rest-call", io);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn))));
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
    public async Task DeployTime_RejectsMalformedInputMapping(string source, string target)
    {
        var io = $@"<zeebe:input source=""{System.Security.SecurityElement.Escape(source)}"" target=""{System.Security.SecurityElement.Escape(target)}"" />";
        var bpmn = CreateBpmnWithCustomServiceTask("rest-call", io);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn))));
    }
}
