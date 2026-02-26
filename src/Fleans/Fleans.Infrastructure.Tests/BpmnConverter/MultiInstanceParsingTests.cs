using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MultiInstanceParsingTests : BpmnConverterTestBase
{
    [TestMethod]
    public async Task Parse_ScriptTask_WithMultiInstanceCardinality()
    {
        // Arrange
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

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        // Assert
        var scriptActivity = workflow.Activities.OfType<ScriptTask>().First();
        Assert.IsNotNull(scriptActivity.LoopCharacteristics, "Should have loop characteristics");
        Assert.IsFalse(scriptActivity.LoopCharacteristics.IsSequential);
        Assert.AreEqual(5, scriptActivity.LoopCharacteristics.LoopCardinality);
        Assert.IsNull(scriptActivity.LoopCharacteristics.InputCollection);
    }

    [TestMethod]
    public async Task Parse_ScriptTask_WithMultiInstanceCollection()
    {
        // Arrange
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.result = _context.item</script>
      <multiInstanceLoopCharacteristics isSequential=""false""
        zeebe:collection=""items"" zeebe:elementVariable=""item""
        zeebe:outputCollection=""results"" zeebe:outputElement=""result"" />
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        // Assert
        var scriptActivity = workflow.Activities.OfType<ScriptTask>().First();
        Assert.IsNotNull(scriptActivity.LoopCharacteristics);
        Assert.AreEqual("items", scriptActivity.LoopCharacteristics.InputCollection);
        Assert.AreEqual("item", scriptActivity.LoopCharacteristics.InputDataItem);
        Assert.AreEqual("results", scriptActivity.LoopCharacteristics.OutputCollection);
        Assert.AreEqual("result", scriptActivity.LoopCharacteristics.OutputDataItem);
    }

    [TestMethod]
    public async Task Parse_ScriptTask_WithSequentialMultiInstance()
    {
        // Arrange
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <scriptTask id=""script"" scriptFormat=""csharp"">
      <script>_context.x = 1</script>
      <multiInstanceLoopCharacteristics isSequential=""true"">
        <loopCardinality>3</loopCardinality>
      </multiInstanceLoopCharacteristics>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""script"" />
    <sequenceFlow id=""f2"" sourceRef=""script"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        // Assert
        var scriptActivity = workflow.Activities.OfType<ScriptTask>().First();
        Assert.IsNotNull(scriptActivity.LoopCharacteristics);
        Assert.IsTrue(scriptActivity.LoopCharacteristics.IsSequential);
        Assert.AreEqual(3, scriptActivity.LoopCharacteristics.LoopCardinality);
    }

    [TestMethod]
    public async Task Parse_ScriptTask_WithoutMultiInstance_HasNullLoopCharacteristics()
    {
        // Arrange
        var bpmn = CreateBpmnWithScriptTask("proc1", "script", "csharp", "_context.x = 1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        // Assert
        var scriptActivity = workflow.Activities.OfType<ScriptTask>().First();
        Assert.IsNull(scriptActivity.LoopCharacteristics);
    }

    [TestMethod]
    public async Task Parse_SubProcess_WithMultiInstanceCardinality()
    {
        // Arrange
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <subProcess id=""sub1"">
      <multiInstanceLoopCharacteristics isSequential=""false"">
        <loopCardinality>3</loopCardinality>
      </multiInstanceLoopCharacteristics>
      <startEvent id=""sub_start"" />
      <task id=""sub_task"" />
      <endEvent id=""sub_end"" />
      <sequenceFlow id=""sf1"" sourceRef=""sub_start"" targetRef=""sub_task"" />
      <sequenceFlow id=""sf2"" sourceRef=""sub_task"" targetRef=""sub_end"" />
    </subProcess>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""sub1"" />
    <sequenceFlow id=""f2"" sourceRef=""sub1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        // Assert
        var subProcess = workflow.Activities.OfType<SubProcess>().First();
        Assert.IsNotNull(subProcess.LoopCharacteristics, "SubProcess should have loop characteristics");
        Assert.IsFalse(subProcess.LoopCharacteristics.IsSequential);
        Assert.AreEqual(3, subProcess.LoopCharacteristics.LoopCardinality);
        // Verify child activities are still parsed correctly
        Assert.AreEqual(3, subProcess.Activities.Count);
    }

    [TestMethod]
    public async Task Parse_CallActivity_WithMultiInstanceCollection()
    {
        // Arrange
        var bpmn = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0""
             id=""def1"" targetNamespace=""test"">
  <process id=""proc1"" isExecutable=""true"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"">
      <multiInstanceLoopCharacteristics isSequential=""true""
        zeebe:collection=""orders"" zeebe:elementVariable=""order""
        zeebe:outputCollection=""results"" zeebe:outputElement=""result"" />
    </callActivity>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""f2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmn)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().First();
        Assert.IsNotNull(callActivity.LoopCharacteristics);
        Assert.IsTrue(callActivity.LoopCharacteristics.IsSequential);
        Assert.AreEqual("orders", callActivity.LoopCharacteristics.InputCollection);
        Assert.AreEqual("order", callActivity.LoopCharacteristics.InputDataItem);
        Assert.AreEqual("results", callActivity.LoopCharacteristics.OutputCollection);
        Assert.AreEqual("result", callActivity.LoopCharacteristics.OutputDataItem);
    }
}
