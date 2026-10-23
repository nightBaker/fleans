using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

// Pools, lanes and data objects from the editor palette (Orbit redesign): a collaboration deploys
// its executable process, and the documentation-only elements don't break the conversion.
[TestClass]
public class CollaborationParsingTests : BpmnConverterTestBase
{
    private const string Collaboration = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"" id=""d"" targetNamespace=""x"">
  <collaboration id=""collab"">
    <participant id=""poolCustomer"" name=""Customer"" processRef=""customer"" />
    <participant id=""poolBank"" name=""Bank"" processRef=""loan"" />
    <messageFlow id=""mf1"" sourceRef=""poolCustomer"" targetRef=""start"" />
  </collaboration>
  <process id=""customer"" isExecutable=""false"" />
  <process id=""loan"" isExecutable=""true"">
    <laneSet id=""lanes"">
      <lane id=""laneScoring"" name=""Scoring""><flowNodeRef>start</flowNodeRef><flowNodeRef>score</flowNodeRef></lane>
      <lane id=""laneManager"" name=""Manager""><flowNodeRef>end</flowNodeRef></lane>
    </laneSet>
    <dataObject id=""applicationData"" />
    <dataObjectReference id=""applicationRef"" name=""Application"" dataObjectRef=""applicationData"" />
    <dataStoreReference id=""crm"" name=""CRM"" />
    <startEvent id=""start"" />
    <scriptTask id=""score"" scriptFormat=""csharp"">
      <dataInputAssociation id=""in1""><sourceRef>applicationRef</sourceRef></dataInputAssociation>
      <dataOutputAssociation id=""out1""><targetRef>crm</targetRef></dataOutputAssociation>
      <script>_context.score = 700</script>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""score"" />
    <sequenceFlow id=""f2"" sourceRef=""score"" targetRef=""end"" />
    <textAnnotation id=""note""><text>Scoring rules v2</text></textAnnotation>
    <association id=""a1"" sourceRef=""score"" targetRef=""note"" />
  </process>
</definitions>";

    [TestMethod]
    public async Task ConvertFromXmlAsync_Collaboration_DeploysTheExecutableProcess()
    {
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(Collaboration)));

        Assert.AreEqual("loan", workflow.WorkflowId);
        CollectionAssert.AreEquivalent(
            new[] { "start", "score", "end" },
            workflow.Activities.Select(a => a.ActivityId).ToArray());
        Assert.HasCount(2, workflow.SequenceFlows);
        Assert.IsInstanceOfType<ScriptTask>(workflow.Activities.Single(a => a.ActivityId == "score"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_WithoutExecutableFlag_PicksTheProcessWithAStartEvent()
    {
        var xml = Collaboration.Replace(@"isExecutable=""true""", "").Replace(@"isExecutable=""false""", "");

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.AreEqual("loan", workflow.WorkflowId);
    }
}
