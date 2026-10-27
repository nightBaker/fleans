using System.Text;
using Fleans.Domain;
using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

[TestClass]
public class IoMappingParsingTests : BpmnConverterTestBase
{
    private const string IoMapping = """
        <extensionElements>
          <fleans:ioMapping>
            <fleans:input source="=order.Total * 1.12m" target="totalWithVat"/>
            <fleans:output source="=result.Status" target="orderStatus"/>
          </fleans:ioMapping>
        </extensionElements>
        """;

    private const string OutputOnly = """
        <extensionElements>
          <fleans:ioMapping><fleans:output source="=payload.Id" target="id"/></fleans:ioMapping>
        </extensionElements>
        """;

    private const string InputOnly = """
        <extensionElements>
          <fleans:ioMapping><fleans:input source="=orderId" target="id"/></fleans:ioMapping>
        </extensionElements>
        """;

    private Task<WorkflowDefinition> Convert(string processBody) =>
        _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:fleans="https://fleans.io/schema/bpmn/1.0"
                         xmlns:zeebe="http://camunda.org/schema/zeebe/1.0">
              <message id="msg" name="order-paid"/>
              <signal id="sig" name="order-shipped"/>
              <process id="p">
                {processBody}
              </process>
            </definitions>
            """)));

    private static Activity Find(IWorkflowDefinition definition, string id) =>
        ((IWorkflowDefinition)definition).GetActivityAcrossScopes(id);

    [TestMethod]
    [DataRow("task")]
    [DataRow("userTask")]
    [DataRow("scriptTask")]
    [DataRow("serviceTask")]
    [DataRow("callActivity")]
    public async Task TasksAndCallActivity_ParseInputsAndOutputs(string element)
    {
        var workflow = await Convert($"""
            <startEvent id="start"/>
            <{element} id="t1" calledElement="child">{IoMapping}</{element}>
            <endEvent id="end"/>
            <sequenceFlow id="f1" sourceRef="start" targetRef="t1"/>
            <sequenceFlow id="f2" sourceRef="t1" targetRef="end"/>
            """);

        var activity = Find(workflow, "t1");
        Assert.AreEqual(new InputMapping("=order.Total * 1.12m", "totalWithVat"), activity.InputMappings.Single());
        Assert.AreEqual(new OutputMapping("=result.Status", "orderStatus"), activity.OutputMappings.Single());
        // Sequence flows reference the mapped activity, so the engine finds its outgoing flow.
        Assert.AreSame(activity, workflow.SequenceFlows.Single(f => f.SequenceFlowId == "f2").Source);
    }

    [TestMethod]
    public async Task CustomServiceTask_KeepsItsTaskTypeAndGetsMappings()
    {
        var workflow = await Convert($"""
            <startEvent id="start"/>
            <serviceTask id="t1" type="rest-call">{IoMapping}</serviceTask>
            <sequenceFlow id="f1" sourceRef="start" targetRef="t1"/>
            """);

        var task = (CustomTaskActivity)Find(workflow, "t1");
        Assert.AreEqual("rest-call", task.TaskType);
        Assert.HasCount(1, task.InputMappings);
        Assert.HasCount(1, task.OutputMappings);
    }

    [TestMethod]
    public async Task SubProcess_ParsesMappings_AndItsChildrenTheirOwn()
    {
        var workflow = await Convert($"""
            <startEvent id="start"/>
            <subProcess id="sub">
              {IoMapping}
              <startEvent id="subStart"/>
              <scriptTask id="inner">{InputOnly}<script>x = 1</script></scriptTask>
              <endEvent id="subEnd"/>
              <sequenceFlow id="s1" sourceRef="subStart" targetRef="inner"/>
              <sequenceFlow id="s2" sourceRef="inner" targetRef="subEnd"/>
            </subProcess>
            <sequenceFlow id="f1" sourceRef="start" targetRef="sub"/>
            """);

        var sub = (SubProcess)Find(workflow, "sub");
        Assert.HasCount(1, sub.InputMappings);
        Assert.HasCount(1, sub.OutputMappings);
        var inner = sub.GetActivity("inner");
        Assert.AreEqual("id", inner.InputMappings.Single().Target);
        Assert.AreSame(inner, sub.SequenceFlows.Single(f => f.SequenceFlowId == "s2").Source);
    }

    [TestMethod]
    public async Task MultiInstance_PutsMappingsOnTheInnerActivity()
    {
        var workflow = await Convert($"""
            <startEvent id="start"/>
            <scriptTask id="t1">
              {IoMapping}
              <multiInstanceLoopCharacteristics><loopCardinality>3</loopCardinality></multiInstanceLoopCharacteristics>
              <script>x = 1</script>
            </scriptTask>
            <sequenceFlow id="f1" sourceRef="start" targetRef="t1"/>
            """);

        var mi = (MultiInstanceActivity)Find(workflow, "t1");
        Assert.HasCount(0, mi.InputMappings);
        Assert.HasCount(1, mi.InnerActivity.InputMappings);
        Assert.HasCount(1, mi.InnerActivity.OutputMappings);
    }

    [TestMethod]
    public async Task Events_ParseTheMappingsCamundaAllows()
    {
        var workflow = await Convert($"""
            <startEvent id="start">{OutputOnly}</startEvent>
            <intermediateCatchEvent id="catch">{OutputOnly}<messageEventDefinition messageRef="msg"/></intermediateCatchEvent>
            <intermediateThrowEvent id="throw">{InputOnly}<signalEventDefinition signalRef="sig"/></intermediateThrowEvent>
            <task id="work"/>
            <boundaryEvent id="timeout" attachedToRef="work">{OutputOnly}<timerEventDefinition><timeDuration>PT1H</timeDuration></timerEventDefinition></boundaryEvent>
            <endEvent id="end">{InputOnly}</endEvent>
            <sequenceFlow id="f1" sourceRef="start" targetRef="catch"/>
            <sequenceFlow id="f2" sourceRef="catch" targetRef="throw"/>
            <sequenceFlow id="f3" sourceRef="throw" targetRef="work"/>
            <sequenceFlow id="f4" sourceRef="work" targetRef="end"/>
            """);

        Assert.AreEqual("id", Find(workflow, "start").OutputMappings.Single().Target);
        Assert.AreEqual("id", Find(workflow, "catch").OutputMappings.Single().Target);
        Assert.AreEqual("id", Find(workflow, "throw").InputMappings.Single().Target);
        Assert.AreEqual("id", Find(workflow, "timeout").OutputMappings.Single().Target);
        Assert.AreEqual("id", Find(workflow, "end").InputMappings.Single().Target);
    }

    [TestMethod]
    public async Task ZeebeIoMapping_IsReadByTheSameParser()
    {
        var workflow = await Convert("""
            <startEvent id="start"/>
            <scriptTask id="t1">
              <extensionElements>
                <zeebe:ioMapping><zeebe:input source="=a.b" target="c"/></zeebe:ioMapping>
              </extensionElements>
              <script>x = 1</script>
            </scriptTask>
            <sequenceFlow id="f1" sourceRef="start" targetRef="t1"/>
            """);

        Assert.AreEqual(new InputMapping("=a.b", "c"), Find(workflow, "t1").InputMappings.Single());
    }

    [TestMethod]
    [DataRow($"<startEvent id=\"x\">{InputOnly}</startEvent>", DisplayName = "Input on start event")]
    [DataRow($"<startEvent id=\"s\"/><endEvent id=\"x\">{OutputOnly}</endEvent>", DisplayName = "Output on end event")]
    [DataRow($"<startEvent id=\"s\"/><intermediateThrowEvent id=\"x\">{OutputOnly}<signalEventDefinition signalRef=\"sig\"/></intermediateThrowEvent>", DisplayName = "Output on throw event")]
    [DataRow($"<startEvent id=\"s\"/><exclusiveGateway id=\"x\">{InputOnly}</exclusiveGateway>", DisplayName = "Input on gateway")]
    public async Task MappingOnAnUnsupportedElement_IsRejected(string processBody)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Convert(processBody));
        StringAssert.Contains(ex.Message, "'x'");
    }

    [TestMethod]
    public async Task MappingOnAnEventSubProcess_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Convert($"""
            <startEvent id="start"/>
            <subProcess id="esp" triggeredByEvent="true">
              {InputOnly}
              <startEvent id="espStart"><messageEventDefinition messageRef="msg"/></startEvent>
            </subProcess>
            """));
    }
}
