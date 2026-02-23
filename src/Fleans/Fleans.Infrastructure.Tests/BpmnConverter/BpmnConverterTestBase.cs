using Fleans.Infrastructure.Bpmn;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

public abstract class BpmnConverterTestBase
{
    protected Bpmn.BpmnConverter _converter = null!;

    [TestInitialize]
    public void Setup()
    {
        _converter = new Bpmn.BpmnConverter();
    }

    protected static string CreateSimpleBpmnXml(string processId, string startEventId, string endEventId, string sequenceFlowId)
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

    protected static string CreateBpmnWithTask(string processId, string taskId)
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

    protected static string CreateBpmnWithUserTask(string processId, string userTaskId)
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

    protected static string CreateBpmnWithServiceTask(string processId, string serviceTaskId)
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

    protected static string CreateBpmnWithExclusiveGateway(string processId, string gatewayId)
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

    protected static string CreateBpmnWithParallelGateway(string processId, string gatewayId, bool isFork)
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

    protected static string CreateBpmnWithConditionalFlow(string processId, string flowId, string condition)
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

    protected static string CreateComplexBpmnWorkflow(string processId)
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

    protected static string CreateBpmnWithInvalidFlow(string processId)
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

    protected static string CreateBpmnWithMultipleConditionalFlows(string processId, string gatewayId)
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

    protected static string CreateBpmnWithAllActivityTypes(string processId)
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

    protected static string CreateBpmnWithScriptTask(string processId, string scriptTaskId, string? scriptFormat, string scriptBody)
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

    protected static string CreateBpmnWithMessageCatchEvent(string processId, string messageId, string messageName)
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

    protected static string CreateBpmnWithMessageBoundaryEvent(string processId, string messageId, string messageName)
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

    protected static string CreateBpmnWithEventBasedGateway(string processId, string gatewayId)
    {
        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"">
  <message id=""msg1"" name=""paymentReceived"" />
  <process id=""{processId}"">
    <startEvent id=""start"" />
    <eventBasedGateway id=""{gatewayId}"" />
    <intermediateCatchEvent id=""timerCatch"">
      <timerEventDefinition><timeDuration>PT1H</timeDuration></timerEventDefinition>
    </intermediateCatchEvent>
    <intermediateCatchEvent id=""msgCatch"">
      <messageEventDefinition messageRef=""msg1"" />
    </intermediateCatchEvent>
    <endEvent id=""end1"" />
    <endEvent id=""end2"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""{gatewayId}"" />
    <sequenceFlow id=""f2"" sourceRef=""{gatewayId}"" targetRef=""timerCatch"" />
    <sequenceFlow id=""f3"" sourceRef=""{gatewayId}"" targetRef=""msgCatch"" />
    <sequenceFlow id=""f4"" sourceRef=""timerCatch"" targetRef=""end1"" />
    <sequenceFlow id=""f5"" sourceRef=""msgCatch"" targetRef=""end2"" />
  </process>
</definitions>";
    }
}
