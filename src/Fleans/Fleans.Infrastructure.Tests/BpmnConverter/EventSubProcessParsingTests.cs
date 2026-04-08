using System.Text;
using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Tests.BpmnConverter;

/// <summary>
/// Slice #A coverage for issue #227 — Event Sub-Process domain model + BPMN parsing.
/// Execution semantics (registration, firing, completion) are validated by later
/// slices' tests.
/// </summary>
[TestClass]
public class EventSubProcessParsingTests : BpmnConverterTestBase
{
    private const string MessageHeader =
        @"<?xml version=""1.0"" encoding=""UTF-8""?>
<definitions xmlns=""http://www.omg.org/spec/BPMN/20100524/MODEL""
             xmlns:zeebe=""http://camunda.org/schema/zeebe/1.0"">";

    private static string MessageEventSubProcess(bool? isInterrupting)
    {
        var attr = isInterrupting is null ? "" : $@" isInterrupting=""{(isInterrupting.Value ? "true" : "false")}""";
        return $@"{MessageHeader}
  <message id=""msg1"" name=""trigger"">
    <zeebe:subscription correlationKey=""= key"" />
  </message>
  <process id=""p"">
    <startEvent id=""start"" />
    <task id=""t1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""t1"" />
    <sequenceFlow id=""f2"" sourceRef=""t1"" targetRef=""end"" />
    <subProcess id=""evtSub"" triggeredByEvent=""true"">
      <startEvent id=""evtStart""{attr}>
        <messageEventDefinition messageRef=""msg1"" />
      </startEvent>
      <scriptTask id=""handler"" scriptFormat=""csharp"">
        <script>x = 1;</script>
      </scriptTask>
      <endEvent id=""evtEnd"" />
      <sequenceFlow id=""ef1"" sourceRef=""evtStart"" targetRef=""handler"" />
      <sequenceFlow id=""ef2"" sourceRef=""handler"" targetRef=""evtEnd"" />
    </subProcess>
  </process>
</definitions>";
    }

    private static string ErrorEventSubProcess(string? errorRef, bool? isInterrupting)
    {
        var attr = isInterrupting is null ? "" : $@" isInterrupting=""{(isInterrupting.Value ? "true" : "false")}""";
        var refAttr = errorRef is null ? "" : $@" errorRef=""{errorRef}""";
        return $@"{MessageHeader}
  <process id=""p"">
    <startEvent id=""start"" />
    <task id=""t1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""t1"" />
    <sequenceFlow id=""f2"" sourceRef=""t1"" targetRef=""end"" />
    <subProcess id=""evtSub"" triggeredByEvent=""true"">
      <startEvent id=""evtStart""{attr}>
        <errorEventDefinition{refAttr} />
      </startEvent>
      <endEvent id=""evtEnd"" />
      <sequenceFlow id=""ef1"" sourceRef=""evtStart"" targetRef=""evtEnd"" />
    </subProcess>
  </process>
</definitions>";
    }

    [TestMethod]
    public async Task ConvertFromXml_TriggeredByEvent_ProducesEventSubProcess()
    {
        var xml = MessageEventSubProcess(isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var evtSub = workflow.Activities.OfType<EventSubProcess>().FirstOrDefault();
        Assert.IsNotNull(evtSub, "Expected an EventSubProcess in the parsed workflow");
        Assert.AreEqual("evtSub", evtSub!.ActivityId);
        Assert.IsFalse(workflow.Activities.OfType<SubProcess>().Any(s => s.ActivityId == "evtSub"),
            "An event sub-process must NOT also be parsed as a regular SubProcess");
    }

    [TestMethod]
    public async Task ConvertFromXml_EventSubProcess_DefaultsToInterrupting()
    {
        var xml = MessageEventSubProcess(isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var evtSub = workflow.Activities.OfType<EventSubProcess>().Single();
        Assert.IsTrue(evtSub.IsInterrupting, "Default isInterrupting should be true");
    }

    [TestMethod]
    public async Task ConvertFromXml_EventSubProcess_RespectsNonInterruptingFlag()
    {
        var xml = MessageEventSubProcess(isInterrupting: false);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var evtSub = workflow.Activities.OfType<EventSubProcess>().Single();
        Assert.IsFalse(evtSub.IsInterrupting);
    }

    [TestMethod]
    public async Task ConvertFromXml_EventSubProcess_ContainsChildActivitiesAndFlows()
    {
        var xml = MessageEventSubProcess(isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var evtSub = workflow.Activities.OfType<EventSubProcess>().Single();
        Assert.IsTrue(evtSub.Activities.Any(a => a.ActivityId == "evtStart"));
        Assert.IsTrue(evtSub.Activities.Any(a => a.ActivityId == "handler"));
        Assert.IsTrue(evtSub.Activities.Any(a => a.ActivityId == "evtEnd"));
        Assert.AreEqual(2, evtSub.SequenceFlows.Count);

        // Children must NOT leak to the root scope
        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "evtStart"));
        Assert.IsFalse(workflow.Activities.Any(a => a.ActivityId == "handler"));
    }

    [TestMethod]
    public async Task ConvertFromXml_EventSubProcess_HasNoIncomingOrOutgoingRootFlows()
    {
        var xml = MessageEventSubProcess(isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.IsFalse(workflow.SequenceFlows.Any(sf => sf.Source.ActivityId == "evtSub"
                                                       || sf.Target.ActivityId == "evtSub"),
            "Event sub-processes must not participate in normal sequence flow at the root scope");
    }

    [TestMethod]
    public async Task ConvertFromXml_RegularSubProcess_StillParsedAsSubProcess()
    {
        var xml = CreateBpmnWithSubProcess("p", "sub1");
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.IsNotNull(workflow.Activities.OfType<SubProcess>().FirstOrDefault(s => s.ActivityId == "sub1"));
        Assert.IsFalse(workflow.Activities.OfType<EventSubProcess>().Any(),
            "A subProcess without triggeredByEvent must not become an EventSubProcess");
    }

    [TestMethod]
    public async Task ConvertFromXml_ErrorStartEventInsideEventSubProcess_IsParsed()
    {
        var xml = ErrorEventSubProcess(errorRef: "BOOM", isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var evtSub = workflow.Activities.OfType<EventSubProcess>().Single();
        var err = evtSub.Activities.OfType<ErrorStartEvent>().SingleOrDefault();
        Assert.IsNotNull(err);
        Assert.AreEqual("evtStart", err!.ActivityId);
        Assert.AreEqual("BOOM", err.ErrorCode);
    }

    [TestMethod]
    public async Task ConvertFromXml_ErrorStartEvent_ResolvesErrorRefToErrorCodeValue()
    {
        // Regression for #272: the parser must resolve the errorRef (id of an <error>
        // element) to the referenced error's errorCode value, not pass the id through.
        var xml = $@"{MessageHeader}
  <error id=""Err500"" errorCode=""500"" name=""ServerError"" />
  <process id=""p"">
    <startEvent id=""start"" />
    <task id=""t1"" />
    <endEvent id=""end"" />
    <sequenceFlow id=""f1"" sourceRef=""start"" targetRef=""t1"" />
    <sequenceFlow id=""f2"" sourceRef=""t1"" targetRef=""end"" />
    <subProcess id=""evtSub"" triggeredByEvent=""true"">
      <startEvent id=""evtStart"">
        <errorEventDefinition errorRef=""Err500"" />
      </startEvent>
      <endEvent id=""evtEnd"" />
      <sequenceFlow id=""ef1"" sourceRef=""evtStart"" targetRef=""evtEnd"" />
    </subProcess>
  </process>
</definitions>";
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var err = workflow.Activities.OfType<EventSubProcess>().Single()
            .Activities.OfType<ErrorStartEvent>().Single();
        Assert.AreEqual("500", err.ErrorCode,
            "errorRef 'Err500' must resolve to the referenced <error> element's errorCode value '500'");
    }

    [TestMethod]
    public async Task ConvertFromXml_ErrorStartEvent_CatchAllHasNullCode()
    {
        var xml = ErrorEventSubProcess(errorRef: null, isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var err = workflow.Activities.OfType<EventSubProcess>().Single()
            .Activities.OfType<ErrorStartEvent>().Single();
        Assert.IsNull(err.ErrorCode);
    }

    [TestMethod]
    public async Task ConvertFromXml_ErrorEventSubProcess_IsAlwaysInterrupting_EvenWhenAttributeSaysFalse()
    {
        // Per BPMN 2.0 §10.2.4 — error start events are always interrupting; the
        // parser must force IsInterrupting=true regardless of the XML attribute.
        var xml = ErrorEventSubProcess(errorRef: "BOOM", isInterrupting: false);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var evtSub = workflow.Activities.OfType<EventSubProcess>().Single();
        Assert.IsTrue(evtSub.IsInterrupting,
            "Error event sub-processes must be interrupting per BPMN 2.0");
    }

    [TestMethod]
    public async Task ConvertFromXml_EventSubProcess_DoesNotBreakRootStartActivityResolution()
    {
        var xml = MessageEventSubProcess(isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        // The root start event must still be resolvable; the event sub-process
        // start event must not be considered the workflow start.
        var start = ((Fleans.Domain.IWorkflowDefinition)workflow).GetStartActivity();
        Assert.AreEqual("start", start.ActivityId);
    }

    [TestMethod]
    public async Task ConvertFromXml_EventSubProcess_IsResolvableViaGetActivity()
    {
        // Reviewer-suggested coverage: later slices need a known-good lookup path
        // for the event sub-process by ID from the root workflow definition.
        var xml = MessageEventSubProcess(isInterrupting: null);
        var workflow = await _converter.ConvertFromXmlAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        var def = (Fleans.Domain.IWorkflowDefinition)workflow;
        var resolved = def.GetActivity("evtSub");
        Assert.IsInstanceOfType(resolved, typeof(EventSubProcess));
        Assert.AreEqual("evtSub", resolved.ActivityId);
    }
}
