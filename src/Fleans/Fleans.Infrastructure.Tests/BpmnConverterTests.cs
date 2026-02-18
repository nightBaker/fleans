using Fleans.Domain;
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
    public async Task ConvertFromXmlAsync_ShouldParseParallelGateway_AsFork_WhenEqualSingleIncomingAndOutgoing()
    {
        // Arrange — 1 incoming, 1 outgoing = pass-through, treated as fork
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-passthrough"">
    <startEvent id=""start"" />
    <parallelGateway id=""gw1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""gw1"" />
    <sequenceFlow id=""flow2"" sourceRef=""gw1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var gateway = workflow.Activities.OfType<ParallelGateway>().FirstOrDefault(g => g.ActivityId == "gw1");
        Assert.IsNotNull(gateway);
        Assert.IsTrue(gateway.IsFork, "A 1:1 parallel gateway should be treated as a fork (pass-through)");
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrow_WhenParallelGatewayHasEqualMultipleIncomingAndOutgoing()
    {
        // Arrange — 2 incoming, 2 outgoing = mixed gateway, not supported
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-mixed"">
    <task id=""task1"" />
    <task id=""task2"" />
    <parallelGateway id=""gw1"" />
    <task id=""task3"" />
    <task id=""task4"" />
    <sequenceFlow id=""flow1"" sourceRef=""task1"" targetRef=""gw1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task2"" targetRef=""gw1"" />
    <sequenceFlow id=""flow3"" sourceRef=""gw1"" targetRef=""task3"" />
    <sequenceFlow id=""flow4"" sourceRef=""gw1"" targetRef=""task4"" />
  </process>
</definitions>";

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        });
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

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldNotConvertBareVariablesInScript()
    {
        // Arrange — script uses _context.var directly, bare identifiers like Math should not be converted
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script4", "script1", "csharp", "_context.x = 42");

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert — bare variable conversion should NOT be applied to scripts
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("_context.x = 42", scriptTask.Script);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseScriptTask_WithMissingScriptElement()
    {
        // Arrange — scriptTask with no <script> child should produce empty script
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow_script5"">
    <startEvent id=""start"" />
    <scriptTask id=""script1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""script1"" />
    <sequenceFlow id=""flow2"" sourceRef=""script1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var scriptTask = workflow.Activities.OfType<ScriptTask>().FirstOrDefault();
        Assert.IsNotNull(scriptTask);
        Assert.AreEqual("", scriptTask.Script);
        Assert.AreEqual("csharp", scriptTask.ScriptFormat);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldThrow_WhenScriptTaskHasUnsupportedFormat()
    {
        // Arrange
        var bpmnXml = CreateBpmnWithScriptTask("workflow_script6", "script1", "javascript", "${x} = 1");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));
        });
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseDefaultFlow_OnExclusiveGateway()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""workflow-default"">
    <startEvent id=""start"" />
    <exclusiveGateway id=""gw1"" default=""flowDefault"" />
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <endEvent id=""endDefault"" />
    <sequenceFlow id=""flow0"" sourceRef=""start"" targetRef=""gw1"" />
    <sequenceFlow id=""flow1"" sourceRef=""gw1"" targetRef=""end1"">
      <conditionExpression>${x > 10}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flow2"" sourceRef=""gw1"" targetRef=""end2"">
      <conditionExpression>${x > 5}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flowDefault"" sourceRef=""gw1"" targetRef=""endDefault"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var defaultFlow = workflow.SequenceFlows.OfType<DefaultSequenceFlow>().FirstOrDefault();
        Assert.IsNotNull(defaultFlow, "Should have a DefaultSequenceFlow");
        Assert.AreEqual("flowDefault", defaultFlow.SequenceFlowId);
        Assert.AreEqual("gw1", defaultFlow.Source.ActivityId);
        Assert.AreEqual("endDefault", defaultFlow.Target.ActivityId);

        // Conditional flows should still be ConditionalSequenceFlow
        var conditionalFlows = workflow.SequenceFlows.OfType<ConditionalSequenceFlow>().ToList();
        Assert.AreEqual(2, conditionalFlows.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithMappings()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"">
      <extensionElements>
        <inputMapping source=""orderId"" target=""orderId"" />
        <inputMapping source=""amount"" target=""paymentAmount"" />
        <outputMapping source=""transactionId"" target=""txId"" />
      </extensionElements>
    </callActivity>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
        Assert.IsNotNull(callActivity);
        Assert.AreEqual("call1", callActivity.ActivityId);
        Assert.AreEqual("childProcess", callActivity.CalledProcessKey);
        Assert.AreEqual(2, callActivity.InputMappings.Count);
        Assert.AreEqual("orderId", callActivity.InputMappings[0].Source);
        Assert.AreEqual("orderId", callActivity.InputMappings[0].Target);
        Assert.AreEqual("amount", callActivity.InputMappings[1].Source);
        Assert.AreEqual("paymentAmount", callActivity.InputMappings[1].Target);
        Assert.AreEqual(1, callActivity.OutputMappings.Count);
        Assert.AreEqual("transactionId", callActivity.OutputMappings[0].Source);
        Assert.AreEqual("txId", callActivity.OutputMappings[0].Target);
        // Default propagation flags
        Assert.IsTrue(callActivity.PropagateAllParentVariables);
        Assert.IsTrue(callActivity.PropagateAllChildVariables);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithNoMappings()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
        Assert.IsNotNull(callActivity);
        Assert.AreEqual("childProcess", callActivity.CalledProcessKey);
        Assert.AreEqual(0, callActivity.InputMappings.Count);
        Assert.AreEqual(0, callActivity.OutputMappings.Count);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseCallActivity_WithPropagationFlags()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" propagateAllParentVariables=""false"" propagateAllChildVariables=""false"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var callActivity = workflow.Activities.OfType<CallActivity>().FirstOrDefault();
        Assert.IsNotNull(callActivity);
        Assert.IsFalse(callActivity.PropagateAllParentVariables);
        Assert.IsFalse(callActivity.PropagateAllChildVariables);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseBoundaryErrorEvent_WithErrorCode()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""call1"">
      <errorEventDefinition errorRef=""PaymentFailed"" />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var boundaryEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
        Assert.IsNotNull(boundaryEvent);
        Assert.AreEqual("err1", boundaryEvent.ActivityId);
        Assert.AreEqual("call1", boundaryEvent.AttachedToActivityId);
        Assert.AreEqual("PaymentFailed", boundaryEvent.ErrorCode);

        // Sequence flow from boundary event to errorEnd
        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == boundaryEvent);
        Assert.IsNotNull(flow);
        Assert.AreEqual("errorEnd", flow.Target.ActivityId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseBoundaryErrorEvent_CatchAll_WhenNoErrorRef()
    {
        // Arrange
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""parent-process"">
    <startEvent id=""start"" />
    <callActivity id=""call1"" calledElement=""childProcess"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""call1"">
      <errorEventDefinition />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""call1"" />
    <sequenceFlow id=""flow2"" sourceRef=""call1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

        // Act
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        // Assert
        var boundaryEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
        Assert.IsNotNull(boundaryEvent);
        Assert.IsNull(boundaryEvent.ErrorCode);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithDuration()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeDuration>PT5M</timeDuration>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
        Assert.IsNotNull(timerEvent);
        Assert.AreEqual("timer1", timerEvent.ActivityId);
        Assert.AreEqual(TimerType.Duration, timerEvent.TimerDefinition.Type);
        Assert.AreEqual("PT5M", timerEvent.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithDate()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeDate>2026-03-01T10:00:00Z</timeDate>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
        Assert.IsNotNull(timerEvent);
        Assert.AreEqual(TimerType.Date, timerEvent.TimerDefinition.Type);
        Assert.AreEqual("2026-03-01T10:00:00Z", timerEvent.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerIntermediateCatchEvent_WithCycle()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""timer1"">
      <timerEventDefinition>
        <timeCycle>R3/PT10M</timeCycle>
      </timerEventDefinition>
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""timer1"" />
    <sequenceFlow id=""flow2"" sourceRef=""timer1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerEvent = workflow.Activities.OfType<TimerIntermediateCatchEvent>().FirstOrDefault();
        Assert.IsNotNull(timerEvent);
        Assert.AreEqual(TimerType.Cycle, timerEvent.TimerDefinition.Type);
        Assert.AreEqual("R3/PT10M", timerEvent.TimerDefinition.Expression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseBoundaryTimerEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""timer-workflow"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <endEvent id=""end"" />
    <endEvent id=""timeoutEnd"" />
    <boundaryEvent id=""bt1"" attachedToRef=""task1"">
      <timerEventDefinition>
        <timeDuration>PT30M</timeDuration>
      </timerEventDefinition>
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""bt1"" targetRef=""timeoutEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var boundaryTimer = workflow.Activities.OfType<BoundaryTimerEvent>().FirstOrDefault();
        Assert.IsNotNull(boundaryTimer);
        Assert.AreEqual("bt1", boundaryTimer.ActivityId);
        Assert.AreEqual("task1", boundaryTimer.AttachedToActivityId);
        Assert.AreEqual(TimerType.Duration, boundaryTimer.TimerDefinition.Type);
        Assert.AreEqual("PT30M", boundaryTimer.TimerDefinition.Expression);

        Assert.IsFalse(workflow.Activities.OfType<BoundaryErrorEvent>().Any(b => b.ActivityId == "bt1"));

        var flow = workflow.SequenceFlows.FirstOrDefault(sf => sf.Source == boundaryTimer);
        Assert.IsNotNull(flow);
        Assert.AreEqual("timeoutEnd", flow.Target.ActivityId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseTimerStartEvent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""scheduled-workflow"">
    <startEvent id=""timerStart1"">
      <timerEventDefinition>
        <timeCycle>R/PT1H</timeCycle>
      </timerEventDefinition>
    </startEvent>
    <task id=""task1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""flow1"" sourceRef=""timerStart1"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var timerStart = workflow.Activities.OfType<TimerStartEvent>().FirstOrDefault();
        Assert.IsNotNull(timerStart);
        Assert.AreEqual("timerStart1", timerStart.ActivityId);
        Assert.AreEqual(TimerType.Cycle, timerStart.TimerDefinition.Type);
        Assert.AreEqual("R/PT1H", timerStart.TimerDefinition.Expression);
        Assert.IsFalse(workflow.Activities.Any(a => a is StartEvent && a is not TimerStartEvent && a.ActivityId == "timerStart1"));
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldStillParseBoundaryErrorEvent_WhenErrorDefinitionPresent()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <process id=""error-workflow"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <endEvent id=""end"" />
    <endEvent id=""errorEnd"" />
    <boundaryEvent id=""err1"" attachedToRef=""task1"">
      <errorEventDefinition errorRef=""500"" />
    </boundaryEvent>
    <sequenceFlow id=""flow1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""flow2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""flow3"" sourceRef=""err1"" targetRef=""errorEnd"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var errorEvent = workflow.Activities.OfType<BoundaryErrorEvent>().FirstOrDefault();
        Assert.IsNotNull(errorEvent);
        Assert.AreEqual("err1", errorEvent.ActivityId);
        Assert.AreEqual("task1", errorEvent.AttachedToActivityId);
        Assert.AreEqual("500", errorEvent.ErrorCode);
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

    // ── Message Event Tests ────────────────────────────────────────

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseMessageIntermediateCatchEvent()
    {
        var bpmnXml = CreateBpmnWithMessageCatchEvent("msg-process", "msg1", "paymentReceived");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var msgCatch = workflow.Activities.OfType<MessageIntermediateCatchEvent>().SingleOrDefault();
        Assert.IsNotNull(msgCatch);
        Assert.AreEqual("waitPayment", msgCatch.ActivityId);
        Assert.AreEqual("msg1", msgCatch.MessageDefinitionId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseMessageBoundaryEvent()
    {
        var bpmnXml = CreateBpmnWithMessageBoundaryEvent("msg-boundary-process", "msg1", "cancelOrder");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        var boundaryMsg = workflow.Activities.OfType<MessageBoundaryEvent>().SingleOrDefault();
        Assert.IsNotNull(boundaryMsg);
        Assert.AreEqual("bmsg1", boundaryMsg.ActivityId);
        Assert.AreEqual("task1", boundaryMsg.AttachedToActivityId);
        Assert.AreEqual("msg1", boundaryMsg.MessageDefinitionId);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseZeebeCorrelationKey()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0"">
  <message id=""msg1"" name=""paymentReceived"">
    <extensionElements>
      <zeebe:subscription correlationKey=""= orderId"" />
    </extensionElements>
  </message>
  <process id=""proc1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitPayment"">
      <messageEventDefinition messageRef=""msg1"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitPayment"" />
    <sequenceFlow id=""f2"" sourceRef=""waitPayment"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        Assert.AreEqual(1, workflow.Messages.Count);
        Assert.AreEqual("orderId", workflow.Messages[0].CorrelationKeyExpression);
        Assert.AreEqual("paymentReceived", workflow.Messages[0].Name);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseFleansCorrelationKey()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:fleans=""http://fleans.io/schema/1.0"">
  <message id=""msg1"" name=""paymentReceived"">
    <extensionElements>
      <fleans:subscription correlationKey=""orderId"" />
    </extensionElements>
  </message>
  <process id=""proc1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitPayment"">
      <messageEventDefinition messageRef=""msg1"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitPayment"" />
    <sequenceFlow id=""f2"" sourceRef=""waitPayment"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        Assert.AreEqual(1, workflow.Messages.Count);
        Assert.AreEqual("orderId", workflow.Messages[0].CorrelationKeyExpression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldParseMessageWithNoCorrelationKey()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <process id=""proc1"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitPayment"">
      <messageEventDefinition messageRef=""msg1"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitPayment"" />
    <sequenceFlow id=""f2"" sourceRef=""waitPayment"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        Assert.AreEqual(1, workflow.Messages.Count);
        Assert.IsNull(workflow.Messages[0].CorrelationKeyExpression);
    }

    [TestMethod]
    public async Task ConvertFromXmlAsync_ShouldPopulateMessagesOnDefinition()
    {
        var bpmnXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""payment"" />
  <message id=""msg2"" name=""cancellation"" />
  <process id=""proc1"">
    <startEvent id=""start"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""end"" />
  </process>
</definitions>";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        Assert.AreEqual(2, workflow.Messages.Count);
        Assert.AreEqual("msg1", workflow.Messages[0].Id);
        Assert.AreEqual("payment", workflow.Messages[0].Name);
        Assert.AreEqual("msg2", workflow.Messages[1].Id);
        Assert.AreEqual("cancellation", workflow.Messages[1].Name);
    }

    // ── Message Event Helper Methods ───────────────────────────────

    private static string CreateBpmnWithMessageCatchEvent(string processId, string messageId, string messageName)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""{messageId}"" name=""{messageName}"" />
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <intermediateCatchEvent id=""waitPayment"">
      <messageEventDefinition messageRef=""{messageId}"" />
    </intermediateCatchEvent>
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""waitPayment"" />
    <sequenceFlow id=""f2"" sourceRef=""waitPayment"" targetRef=""end"" />
  </process>
</definitions>";
    }

    private static string CreateBpmnWithMessageBoundaryEvent(string processId, string messageId, string messageName)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""{messageId}"" name=""{messageName}"" />
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <task id=""task1"" />
    <boundaryEvent id=""bmsg1"" attachedToRef=""task1"">
      <messageEventDefinition messageRef=""{messageId}"" />
    </boundaryEvent>
    <endEvent id=""end"" />
    <endEvent id=""msgEnd"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""task1"" />
    <sequenceFlow id=""f2"" sourceRef=""task1"" targetRef=""end"" />
    <sequenceFlow id=""f3"" sourceRef=""bmsg1"" targetRef=""msgEnd"" />
  </process>
</definitions>";
    }
}

