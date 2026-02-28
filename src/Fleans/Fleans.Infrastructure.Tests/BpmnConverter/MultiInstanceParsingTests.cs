using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MultiInstanceParsingTests
{
    [TestMethod]
    public async Task Parse_ScriptTask_WithCardinalityMultiInstance()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
      <multiInstanceLoopCharacteristics isSequential=""false"">
        <loopCardinality>5</loopCardinality>
      </multiInstanceLoopCharacteristics>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        var converter = new Fleans.Infrastructure.Bpmn.BpmnConverter();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var miActivity = workflow.Activities.OfType<MultiInstanceActivity>().FirstOrDefault();
        Assert.IsNotNull(miActivity, "Should have a MultiInstanceActivity");
        Assert.AreEqual("script", miActivity.ActivityId);
        Assert.IsFalse(miActivity.IsSequential);
        Assert.AreEqual(5, miActivity.LoopCardinality);
        Assert.IsNull(miActivity.InputCollection);
        Assert.IsInstanceOfType(miActivity.InnerActivity, typeof(ScriptTask));
    }

    [TestMethod]
    public async Task Parse_ScriptTask_WithCollectionMultiInstance()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.result = _context.item</script>
      <multiInstanceLoopCharacteristics isSequential=""true""
        zeebe:collection=""items"" zeebe:elementVariable=""item""
        zeebe:outputCollection=""results"" zeebe:outputElement=""result"" />
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        var converter = new Fleans.Infrastructure.Bpmn.BpmnConverter();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        var miActivity = workflow.Activities.OfType<MultiInstanceActivity>().FirstOrDefault();
        Assert.IsNotNull(miActivity, "Should have a MultiInstanceActivity");
        Assert.IsTrue(miActivity.IsSequential);
        Assert.AreEqual("items", miActivity.InputCollection);
        Assert.AreEqual("item", miActivity.InputDataItem);
        Assert.AreEqual("results", miActivity.OutputCollection);
        Assert.AreEqual("result", miActivity.OutputDataItem);
    }

    [TestMethod]
    public async Task Parse_TaskWithoutMultiInstance_ShouldNotWrap()
    {
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        var converter = new Fleans.Infrastructure.Bpmn.BpmnConverter();
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(bpmn));
        var workflow = await converter.ConvertFromXmlAsync(stream);

        Assert.IsFalse(workflow.Activities.Any(a => a is MultiInstanceActivity),
            "Should NOT have a MultiInstanceActivity when no loop characteristics present");
    }
}
