using Fleans.Domain.Activities;
using System.Text;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class MessageEventTests : BpmnConverterTestBase
{
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
             xmlns:fleans=""http://fleans.io/schema/bpmn/fleans"">
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
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL"" xmlns:bpmndi=""http://www.omg.org/spec/BPMN/20100524/DI"" xmlns:dc=""http://www.omg.org/spec/DD/20100524/DC"" xmlns:di=""http://www.omg.org/spec/DD/20100524/DI"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" id=""Definitions_1"" targetNamespace=""http://example.org/testProcess"">
  <process id=""procces_event_message"" name=""Test Process"" isExecutable=""true"">
    <exclusiveGateway id=""gw_check_amount"" name=""Check Amount"">
      <incoming>Flow_1n37x1g</incoming>
      <outgoing>flow_high_amount</outgoing>
      <outgoing>flow_low_amount</outgoing>
    </exclusiveGateway>
    <userTask id=""manager_approval"" name=""Manager Approval"">
      <incoming>flow_high_amount</incoming>
      <outgoing>flow_approval_to_merge</outgoing>
    </userTask>
    <exclusiveGateway id=""gw_merge"" name=""Merge"" default=""flow_merge_to_fork"">
      <incoming>flow_approval_to_merge</incoming>
      <incoming>flow_auto_to_merge</incoming>
      <outgoing>flow_merge_to_fork</outgoing>
    </exclusiveGateway>
    <parallelGateway id=""gw_fork"" name=""Fork"">
      <incoming>flow_merge_to_fork</incoming>
      <outgoing>flow_fork_to_email</outgoing>
      <outgoing>flow_fork_to_inventory</outgoing>
    </parallelGateway>
    <task id=""send_email"" name=""Send Confirmation Email"">
      <incoming>flow_fork_to_email</incoming>
      <outgoing>flow_email_to_join</outgoing>
    </task>
    <task id=""update_inventory"" name=""Update Inventory"">
      <incoming>flow_fork_to_inventory</incoming>
      <outgoing>flow_inventory_to_join</outgoing>
    </task>
    <parallelGateway id=""gw_join"" name=""Join"">
      <incoming>flow_email_to_join</incoming>
      <incoming>flow_inventory_to_join</incoming>
      <outgoing>flow_join_to_end</outgoing>
    </parallelGateway>
    <endEvent id=""end"" name=""Order Completed"">
      <incoming>flow_join_to_end</incoming>
    </endEvent>
    <sequenceFlow id=""flow_validate_to_gw1"" sourceRef=""validate_order_1"" targetRef=""Event_0o28sqh"" />
    <sequenceFlow id=""flow_high_amount"" sourceRef=""gw_check_amount"" targetRef=""manager_approval"">
      <conditionExpression xsi:type=""tFormalExpression"">${x &gt; 100}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flow_low_amount"" sourceRef=""gw_check_amount"" targetRef=""auto_approve"">
      <conditionExpression xsi:type=""tFormalExpression"">${x &lt;= 100}</conditionExpression>
    </sequenceFlow>
    <sequenceFlow id=""flow_approval_to_merge"" sourceRef=""manager_approval"" targetRef=""gw_merge"" />
    <sequenceFlow id=""flow_auto_to_merge"" sourceRef=""auto_approve"" targetRef=""gw_merge"" />
    <sequenceFlow id=""flow_merge_to_fork"" sourceRef=""gw_merge"" targetRef=""gw_fork"" />
    <sequenceFlow id=""flow_fork_to_email"" sourceRef=""gw_fork"" targetRef=""send_email"" />
    <sequenceFlow id=""flow_fork_to_inventory"" sourceRef=""gw_fork"" targetRef=""update_inventory"" />
    <sequenceFlow id=""flow_email_to_join"" sourceRef=""send_email"" targetRef=""gw_join"" />
    <sequenceFlow id=""flow_inventory_to_join"" sourceRef=""update_inventory"" targetRef=""gw_join"" />
    <sequenceFlow id=""flow_join_to_end"" sourceRef=""gw_join"" targetRef=""end"" />
    <scriptTask id=""validate_order_1"" name=""Validate Order"">
      <incoming>Flow_0l1ei3t</incoming>
      <outgoing>flow_validate_to_gw1</outgoing>
      <script>_context.x=5;</script>
    </scriptTask>
    <scriptTask id=""auto_approve"" name=""Auto Approve"" scriptFormat=""csharp"">
      <incoming>flow_low_amount</incoming>
      <outgoing>flow_auto_to_merge</outgoing>
      <script>_context.x=500;
_context.y=300;</script>
    </scriptTask>
    <startEvent id=""start"" name=""Order Received"">
      <outgoing>Flow_0l1ei3t</outgoing>
    </startEvent>
    <intermediateCatchEvent id=""Event_0o28sqh"">
      <incoming>flow_validate_to_gw1</incoming>
      <outgoing>Flow_1n37x1g</outgoing>
      <messageEventDefinition id=""MessageEventDefinition_0n6mxee"" messageRef=""Message_Event_0o28sqh"" />
    </intermediateCatchEvent>
    <sequenceFlow id=""Flow_1n37x1g"" sourceRef=""Event_0o28sqh"" targetRef=""gw_check_amount"" />
    <sequenceFlow id=""Flow_0l1ei3t"" sourceRef=""start"" targetRef=""validate_order_1"" />
  </process>
  <message id=""Message_start"" name=""OrderReceive"" />
  <message id=""Message_Event_0o28sqh"" name=""orderValid"" />
  <bpmndi:BPMNDiagram id=""BPMNDiagram_1"">
    <bpmndi:BPMNPlane id=""BPMNPlane_1"" bpmnElement=""procces_event_message"">
      <bpmndi:BPMNShape id=""gw_check_amount_di"" bpmnElement=""gw_check_amount"" isMarkerVisible=""true"">
        <dc:Bounds x=""425"" y=""245"" width=""50"" height=""50"" />
        <bpmndi:BPMNLabel>
          <dc:Bounds x=""485"" y=""263"" width=""72"" height=""14"" />
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""manager_approval_di"" bpmnElement=""manager_approval"">
        <dc:Bounds x=""540"" y=""130"" width=""100"" height=""80"" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""gw_merge_di"" bpmnElement=""gw_merge"" isMarkerVisible=""true"">
        <dc:Bounds x=""705"" y=""245"" width=""50"" height=""50"" />
        <bpmndi:BPMNLabel>
          <dc:Bounds x=""663"" y=""263"" width=""32"" height=""14"" />
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""gw_fork_di"" bpmnElement=""gw_fork"">
        <dc:Bounds x=""825"" y=""245"" width=""50"" height=""50"" />
        <bpmndi:BPMNLabel>
          <dc:Bounds x=""838"" y=""302"" width=""24"" height=""14"" />
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""send_email_di"" bpmnElement=""send_email"">
        <dc:Bounds x=""940"" y=""130"" width=""100"" height=""80"" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""update_inventory_di"" bpmnElement=""update_inventory"">
        <dc:Bounds x=""940"" y=""330"" width=""100"" height=""80"" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""gw_join_di"" bpmnElement=""gw_join"">
        <dc:Bounds x=""1105"" y=""245"" width=""50"" height=""50"" />
        <bpmndi:BPMNLabel>
          <dc:Bounds x=""1118"" y=""302"" width=""24"" height=""14"" />
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""end_di"" bpmnElement=""end"">
        <dc:Bounds x=""1222"" y=""252"" width=""36"" height=""36"" />
        <bpmndi:BPMNLabel>
          <dc:Bounds x=""1198"" y=""295"" width=""84"" height=""14"" />
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""Activity_1l1foat_di"" bpmnElement=""auto_approve"">
        <dc:Bounds x=""540"" y=""330"" width=""100"" height=""80"" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""Event_0v6daj6_di"" bpmnElement=""start"">
        <dc:Bounds x=""12"" y=""252"" width=""36"" height=""36"" />
        <bpmndi:BPMNLabel>
          <dc:Bounds x=""-9"" y=""295"" width=""78"" height=""14"" />
        </bpmndi:BPMNLabel>
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""Activity_0qdtrv5_di"" bpmnElement=""validate_order_1"">
        <dc:Bounds x=""120"" y=""230"" width=""100"" height=""80"" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNShape id=""Event_1q0vy5h_di"" bpmnElement=""Event_0o28sqh"">
        <dc:Bounds x=""312"" y=""262"" width=""36"" height=""36"" />
      </bpmndi:BPMNShape>
      <bpmndi:BPMNEdge id=""flow_validate_to_gw1_di"" bpmnElement=""flow_validate_to_gw1"">
        <di:waypoint x=""220"" y=""270"" />
        <di:waypoint x=""266"" y=""270"" />
        <di:waypoint x=""266"" y=""280"" />
        <di:waypoint x=""310"" y=""280"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_high_amount_di"" bpmnElement=""flow_high_amount"">
        <di:waypoint x=""450"" y=""245"" />
        <di:waypoint x=""450"" y=""170"" />
        <di:waypoint x=""540"" y=""170"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_low_amount_di"" bpmnElement=""flow_low_amount"">
        <di:waypoint x=""450"" y=""295"" />
        <di:waypoint x=""450"" y=""370"" />
        <di:waypoint x=""540"" y=""370"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_approval_to_merge_di"" bpmnElement=""flow_approval_to_merge"">
        <di:waypoint x=""640"" y=""170"" />
        <di:waypoint x=""730"" y=""170"" />
        <di:waypoint x=""730"" y=""245"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_auto_to_merge_di"" bpmnElement=""flow_auto_to_merge"">
        <di:waypoint x=""640"" y=""370"" />
        <di:waypoint x=""730"" y=""370"" />
        <di:waypoint x=""730"" y=""295"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_merge_to_fork_di"" bpmnElement=""flow_merge_to_fork"">
        <di:waypoint x=""755"" y=""270"" />
        <di:waypoint x=""825"" y=""270"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_fork_to_email_di"" bpmnElement=""flow_fork_to_email"">
        <di:waypoint x=""850"" y=""245"" />
        <di:waypoint x=""850"" y=""170"" />
        <di:waypoint x=""940"" y=""170"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_fork_to_inventory_di"" bpmnElement=""flow_fork_to_inventory"">
        <di:waypoint x=""850"" y=""295"" />
        <di:waypoint x=""850"" y=""370"" />
        <di:waypoint x=""940"" y=""370"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_email_to_join_di"" bpmnElement=""flow_email_to_join"">
        <di:waypoint x=""1040"" y=""170"" />
        <di:waypoint x=""1130"" y=""170"" />
        <di:waypoint x=""1130"" y=""245"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_inventory_to_join_di"" bpmnElement=""flow_inventory_to_join"">
        <di:waypoint x=""1040"" y=""370"" />
        <di:waypoint x=""1130"" y=""370"" />
        <di:waypoint x=""1130"" y=""295"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""flow_join_to_end_di"" bpmnElement=""flow_join_to_end"">
        <di:waypoint x=""1155"" y=""270"" />
        <di:waypoint x=""1222"" y=""270"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""Flow_1n37x1g_di"" bpmnElement=""Flow_1n37x1g"">
        <di:waypoint x=""348"" y=""280"" />
        <di:waypoint x=""387"" y=""280"" />
        <di:waypoint x=""387"" y=""270"" />
        <di:waypoint x=""425"" y=""270"" />
      </bpmndi:BPMNEdge>
      <bpmndi:BPMNEdge id=""Flow_0l1ei3t_di"" bpmnElement=""Flow_0l1ei3t"">
        <di:waypoint x=""48"" y=""270"" />
        <di:waypoint x=""120"" y=""270"" />
      </bpmndi:BPMNEdge>
    </bpmndi:BPMNPlane>
  </bpmndi:BPMNDiagram>
</definitions>
";

        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(bpmnXml)));

        Assert.AreEqual(2, workflow.Messages.Count);
        Assert.IsNull(workflow.Messages[0].CorrelationKeyExpression);
        Assert.IsNull(workflow.Messages[1].CorrelationKeyExpression);
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
}
