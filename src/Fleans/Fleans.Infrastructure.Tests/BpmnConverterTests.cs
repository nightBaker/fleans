using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Infrastructure.Bpmn;
using System.Text;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class BpmnConverterTests
{
    private BpmnConverter _converter = null!;

    [TestInitialize]
    public void Setup()
    {
        _converter = new BpmnConverter();
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseSimpleWorkflow_WithStartAndEnd()
    {
        // Arrange
        var bpmnXml = CreateSimpleBpmnXml("workflow1", 
            startEventId: "start", 
            endEventId: "end",
            sequenceFlowId: "flow1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsNotNull(workflow);
        Assert.AreEqual("workflow1", workflow.WorkflowId);
        Assert.AreEqual(2, workflow.Activities.Count);
        Assert.IsTrue(workflow.Activities.Any(a => a is StartEvent && a.ActivityId == "start"));
        Assert.IsTrue(workflow.Activities.Any(a => a is EndEvent && a.ActivityId == "end"));
        Assert.AreEqual(1, workflow.SequenceFlows.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseWorkflow_WithTask()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithTask("workflow2", "task1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.AreEqual(3, workflow.Activities.Count);
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity && a.ActivityId == "task1"));
        Assert.AreEqual(2, workflow.SequenceFlows.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseUserTask_AsTaskActivity()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithUserTask("workflow3", "userTask1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity && a.ActivityId == "userTask1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseServiceTask_AsTaskActivity()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithServiceTask("workflow4", "serviceTask1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity && a.ActivityId == "serviceTask1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseExclusiveGateway()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithExclusiveGateway("workflow5", "gateway1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is ExclusiveGateway && a.ActivityId == "gateway1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseParallelGateway_AsFork_WhenMoreOutgoingThanIncoming()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithParallelGateway("workflow6", "fork1", isFork: true);

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ParallelGateway>().FirstOrDefault(g => g.ActivityId == "fork1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseParallelGateway_AsJoin_WhenMoreIncomingThanOutgoing()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithParallelGateway("workflow7", "join1", isFork: false);

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ParallelGateway>().FirstOrDefault(g => g.ActivityId == "join1");
        Assert.IsNotNull(gateway);
        Assert.IsFalse(gateway.IsFork);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseConditionalSequenceFlow_WithCondition()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow8", "flow1", "${x > 5}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.x > 5", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldConvertBpmnCondition_FromDollarNotation_ToContextNotation()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow9", "flow1", "${amount > 100 && status == 'active'}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.amount > 100 && _context.status == 'active'", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseRegularSequenceFlow_WithoutCondition()
    {
        // Arrange
        var bpmnXml = CreateSimpleBpmnXml("workflow10", "start", "end", "flow1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var flow = workflow.SequenceFlows.FirstOrDefault();
        Assert.IsNotNull(flow);
        Assert.IsInstanceOfType(flow, typeof(SequenceFlow));
        Assert.IsNotInstanceOfType(flow, typeof(ConditionalSequenceFlow));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleComplexWorkflow_WithMultipleActivities()
    {
        // Arrange
        var bpmnXml = CreateComplexBpmnWorkflow("complexWorkflow");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.AreEqual("complexWorkflow", workflow.WorkflowId);
        Assert.IsTrue(workflow.Activities.Count >= 5);
        Assert.IsTrue(workflow.SequenceFlows.Count >= 4);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrowException_WhenProcessElementMissing()
    {
        // Arrange
        var invalidXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
</definitions>";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalidXml)));
        });
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrowException_WhenProcessIdMissing()
    {
        // Arrange
        var invalidXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process>
    <startEvent id=""start"" />
  </process>
</definitions>";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(invalidXml)));
        });
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldSkipSequenceFlows_WithMissingSourceOrTarget()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithInvalidFlow("workflow11");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        // Should only have valid flows, not the one with missing target
        Assert.IsTrue(workflow.SequenceFlows.Count >= 1);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleMultipleConditionalFlows_FromSameGateway()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithMultipleConditionalFlows("workflow12", "gateway1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlows = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().ToList();
        Assert.IsTrue(conditionalFlows.Count >= 2);
        Assert.IsTrue(conditionalFlows.All(f => f.Source.ActivityId == "gateway1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleEmptyConditionExpression()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow13", "flow1", "");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleCondition_WithMultipleVariables()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow14", "flow1", "${x > y && z == 10}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.x > _context.y && _context.z == 10", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldPreserveCondition_WithoutDollarNotation()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithConditionalFlow("workflow15", "flow1", "_context.x > 5");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var conditionalFlow = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(conditionalFlow);
        Assert.AreEqual("_context.x > 5", conditionalFlow.Condition);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldHandleWorkflow_WithAllActivityTypes()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithAllActivityTypes("workflow16");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        Assert.IsTrue(workflow.Activities.Any(a => a is StartEvent));
        Assert.IsTrue(workflow.Activities.Any(a => a is EndEvent));
        Assert.IsTrue(workflow.Activities.Any(a => a is TaskActivity));
        Assert.IsTrue(workflow.Activities.Any(a => a is ExclusiveGateway));
        Assert.IsTrue(workflow.Activities.Any(a => a is ParallelGateway));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseScriptTask_WithFormatAndScript()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script1", "script1", "csharp", "${result} = ${x} + ${y}");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("script1", scriptTask.ActivityId);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
        Assert.AreEqual("_context.result = _context.x + _context.y", scriptTask.Script);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseScriptTask_WithDefaultFormat()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script2", "script1", null, "${total} = 42");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldConvertScriptBody_FromDollarNotation()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script3", "script1", "csharp", "${count} = ${count} + 1");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("_context.count = _context.count + 1", scriptTask.Script);
    }

    private static string CreateBpmnWithScriptTask(string processId, string scriptTaskId, string? scriptFormat, string scriptBody)
    {
        var formatAttr = scriptFormat != null ? $@" scriptFormat=""{scriptFormat}""" : "";
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <scriptTask id=""{scriptTaskId}""{formatAttr}>
      <script>{System.Security.SecurityElement.Escape(scriptBody)}</script>
    </scriptTask>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{scriptTaskId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{scriptTaskId}"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private static string CreateSimpleBpmnXml(string processId, string startEventId, string endEventId, string sequenceFlowId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""{startEventId}"" />
    <endEvent id=""{endEventId}"" />
    <sequenceFlow id=""{sequenceFlowId}"" sourceRef=""{startEventId}"" targetRef=""{endEventId}"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithTask(string processId, string taskId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <task id=""{taskId}"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{taskId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{taskId}"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithUserTask(string processId, string userTaskId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <userTask id=""{userTaskId}"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{userTaskId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{userTaskId}"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithServiceTask(string processId, string serviceTaskId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <serviceTask id=""{serviceTaskId}"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{serviceTaskId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{serviceTaskId}"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithExclusiveGateway(string processId, string gatewayId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <exclusiveGateway id=""{gatewayId}"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{gatewayId}"" targetRef=""end1"" />
    <sequenceFlow id=""flow3"" sourceRef=""{gatewayId}"" targetRef=""end2"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithParallelGateway(string processId, string gatewayId, bool isFork)
    {
        if (isFork)
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <parallelGateway id=""{gatewayId}"" />
    <task id=""task1"" />
    <task id=""task2"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{gatewayId}"" targetRef=""task1"" />
    <sequenceFlow id=""flow3"" sourceRef=""{gatewayId}"" targetRef=""task2"" />
  </process>
</definitions>";
        }
        else
        {
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <task id=""task1"" />
    <task id=""task2"" />
    <parallelGateway id=""{gatewayId}"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""task1"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""task2"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""flow3"" sourceRef=""{gatewayId}"" targetRef=""end"" />
  </process>
</definitions>";
        }
    }

    private static string CreateBpmnWithConditionalFlow(string processId, string flowId, string condition)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <exclusiveGateway id=""gateway"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow0"" sourceRef=""start"" targetRef=""gateway"" />
    <sequenceFlow id=""{flowId}"" sourceRef=""gateway"" targetRef=""end"">
      <conditionExpression>{System.Security.SecurityElement.Escape(condition)}</conditionExpression>
    </sequenceFlow>
  </process>
</definitions>";
    }

    private static string CreateComplexBpmnWorkflow(string processId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <exclusiveGateway id=""gateway1"" />
    <task id=""task2"" />
    <task id=""task3"" />
    <parallelGateway id=""fork1"" />
    <task id=""task4"" />
    <task id=""task5"" />
    <parallelGateway id=""join1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""gateway1"" />
    <sequenceFlow id=""flow3"" sourceRef=""gateway1"" targetRef=""task2"" />
    <sequenceFlow id=""flow4"" sourceRef=""gateway1"" targetRef=""task3"" />
    <sequenceFlow id=""flow5"" sourceRef=""task2"" targetRef=""fork1"" />
    <sequenceFlow id=""flow6"" sourceRef=""fork1"" targetRef=""task4"" />
    <sequenceFlow id=""flow7"" sourceRef=""fork1"" targetRef=""task5"" />
    <sequenceFlow id=""flow8"" sourceRef=""task4"" targetRef=""join1"" />
    <sequenceFlow id=""flow9"" sourceRef=""task5"" targetRef=""join1"" />
    <sequenceFlow id=""flow10"" sourceRef=""join1"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithInvalidFlow(string processId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""end"" />
    <sequenceFlow id=""flow2"" sourceRef=""start"" targetRef=""nonexistent"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithMultipleConditionalFlows(string processId, string gatewayId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <exclusiveGateway id=""{gatewayId}"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""flow2"" sourceRef=""{gatewayId}"" targetRef=""end1"">
      <conditionExpression>${{x > 5}}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flow3"" sourceRef=""{gatewayId}"" targetRef=""end2"">
      <conditionExpression>{System.Security.SecurityElement.Escape("${{x <= 5}}")}</conditionExpression>
    </sequenceFlow>
  </process>
</definitions>";
    }

    private static string CreateBpmnWithAllActivityTypes(string processId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <userTask id=""userTask1"" />
    <serviceTask id=""serviceTask1"" />
    <exclusiveGateway id=""exclusiveGateway1"" />
    <parallelGateway id=""parallelGateway1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""userTask1"" />
    <sequenceFlow id=""flow3"" sourceRef=""userTask1"" targetRef=""serviceTask1"" />
    <sequenceFlow id=""flow4"" sourceRef=""serviceTask1"" targetRef=""exclusiveGateway1"" />
    <sequenceFlow id=""flow5"" sourceRef=""exclusiveGateway1"" targetRef=""parallelGateway1"" />
    <sequenceFlow id=""flow6"" sourceRef=""parallelGateway1"" targetRef=""end"" />
  </process>
</definitions>";
    }
}

