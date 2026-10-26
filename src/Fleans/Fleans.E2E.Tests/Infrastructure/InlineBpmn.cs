namespace Fleans.E2E.Tests.Infrastructure;

/// <summary>
/// Builds minimal BPMN documents with caller-chosen process keys / message / signal names.
/// The admin-UI specs need identifiers that are unique per test run: concurrent E2E stacks
/// share the same SQLite file, so the static fixtures under <c>tests/manual/</c> (fixed
/// process ids and event names) would collide in lists and cross-deliver messages/signals.
/// </summary>
internal static class InlineBpmn
{
    /// <summary>Returns a short unique suffix suitable for process keys and event names.</summary>
    public static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// start → script → end. Completes immediately when started. No diagram layout and no
    /// incoming/outgoing — the Admin UI lays it out itself (bpmnAutoLayout.js).
    /// </summary>
    public static string ScriptOnly(string processKey) => Wrap(string.Empty, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="start" />
            <scriptTask id="work" scriptFormat="csharp"><script>_context.done = true</script></scriptTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
            <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
          </process>
        """);

    /// <summary>
    /// start → script → end with shapes in the diagram but no edges for its flows (a hand-edited
    /// file) — the Admin UI draws the missing edges itself (bpmnAutoLayout.js).
    /// </summary>
    public static string ScriptShapesOnly(string processKey) => Wrap(string.Empty, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="start" />
            <scriptTask id="work" scriptFormat="csharp"><script>_context.done = true</script></scriptTask>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="work" />
            <sequenceFlow id="f2" sourceRef="work" targetRef="end" />
          </process>
          <bpmndi:BPMNDiagram id="BPMNDiagram_1">
            <bpmndi:BPMNPlane id="BPMNPlane_1" bpmnElement="{processKey}">
              <bpmndi:BPMNShape id="start_di" bpmnElement="start"><dc:Bounds x="100" y="100" width="36" height="36" /></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="work_di" bpmnElement="work"><dc:Bounds x="200" y="78" width="100" height="80" /></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="end_di" bpmnElement="end"><dc:Bounds x="360" y="100" width="36" height="36" /></bpmndi:BPMNShape>
            </bpmndi:BPMNPlane>
          </bpmndi:BPMNDiagram>
        """);

    /// <summary>
    /// start → message catch (correlated on the <c>requestId</c> start variable) → end.
    /// Instances stay Running until the message is delivered with the matching key.
    /// Carries its own diagram layout.
    /// </summary>
    public static string MessageCatch(string processKey, string messageName) => Wrap($"""
          <message id="msg1" name="{messageName}">
            <extensionElements><zeebe:subscription correlationKey="= requestId" /></extensionElements>
          </message>
        """, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="start" />
            <intermediateCatchEvent id="waitMessage"><messageEventDefinition messageRef="msg1" /></intermediateCatchEvent>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="waitMessage" />
            <sequenceFlow id="f2" sourceRef="waitMessage" targetRef="end" />
          </process>
          <bpmndi:BPMNDiagram id="Diagram_1">
            <bpmndi:BPMNPlane id="Plane_1" bpmnElement="{processKey}">
              <bpmndi:BPMNShape id="start_di" bpmnElement="start"><dc:Bounds x="100" y="100" width="36" height="36" /></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="waitMessage_di" bpmnElement="waitMessage"><dc:Bounds x="200" y="100" width="36" height="36" /></bpmndi:BPMNShape>
              <bpmndi:BPMNShape id="end_di" bpmnElement="end"><dc:Bounds x="300" y="100" width="36" height="36" /></bpmndi:BPMNShape>
              <bpmndi:BPMNEdge id="f1_di" bpmnElement="f1"><di:waypoint x="136" y="118" /><di:waypoint x="200" y="118" /></bpmndi:BPMNEdge>
              <bpmndi:BPMNEdge id="f2_di" bpmnElement="f2"><di:waypoint x="236" y="118" /><di:waypoint x="300" y="118" /></bpmndi:BPMNEdge>
            </bpmndi:BPMNPlane>
          </bpmndi:BPMNDiagram>
        """);

    /// <summary>start → signal catch → end. Instances stay Running until the signal is broadcast.</summary>
    public static string SignalCatch(string processKey, string signalName) => Wrap($"""
          <signal id="sig1" name="{signalName}" />
        """, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="start" />
            <intermediateCatchEvent id="waitSignal"><signalEventDefinition signalRef="sig1" /></intermediateCatchEvent>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="waitSignal" />
            <sequenceFlow id="f2" sourceRef="waitSignal" targetRef="end" />
          </process>
        """);

    /// <summary>Message start event → end.</summary>
    public static string MessageStart(string processKey, string messageName) => Wrap($"""
          <message id="msg1" name="{messageName}" />
        """, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="msgStart"><messageEventDefinition messageRef="msg1" /></startEvent>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="msgStart" targetRef="end" />
          </process>
        """);

    /// <summary>Signal start event → end.</summary>
    public static string SignalStart(string processKey, string signalName) => Wrap($"""
          <signal id="sig1" name="{signalName}" />
        """, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="sigStart"><signalEventDefinition signalRef="sig1" /></startEvent>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="sigStart" targetRef="end" />
          </process>
        """);

    /// <summary>Conditional start event (<paramref name="condition"/>) → end.</summary>
    public static string ConditionalStart(string processKey, string condition) => Wrap(string.Empty, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="condStart">
              <conditionalEventDefinition><condition>{System.Security.SecurityElement.Escape(condition)}</condition></conditionalEventDefinition>
            </startEvent>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="condStart" targetRef="end" />
          </process>
        """);

    /// <summary>
    /// start → parallel fork → two scripts → complex-gateway join (fires on the first token) → end.
    /// Completes when started. No diagram layout (laid out by the Admin UI).
    /// </summary>
    public static string ComplexJoin(string processKey) => Wrap(string.Empty, $"""
          <process id="{processKey}" isExecutable="true">
            <startEvent id="start" />
            <parallelGateway id="fork" />
            <scriptTask id="taskA" scriptFormat="csharp"><script>_context.a = 1</script></scriptTask>
            <scriptTask id="taskB" scriptFormat="csharp"><script>_context.b = 2</script></scriptTask>
            <complexGateway id="join"><activationCondition>_context._nroftoken &gt;= 1</activationCondition></complexGateway>
            <endEvent id="end" />
            <sequenceFlow id="f1" sourceRef="start" targetRef="fork" />
            <sequenceFlow id="f2" sourceRef="fork" targetRef="taskA" />
            <sequenceFlow id="f3" sourceRef="fork" targetRef="taskB" />
            <sequenceFlow id="f4" sourceRef="taskA" targetRef="join" />
            <sequenceFlow id="f5" sourceRef="taskB" targetRef="join" />
            <sequenceFlow id="f6" sourceRef="join" targetRef="end" />
          </process>
        """);

    private static string Wrap(string rootElements, string process) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
                     xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
                     xmlns:dc="http://www.omg.org/spec/DD/20100524/DC"
                     xmlns:di="http://www.omg.org/spec/DD/20100524/DI"
                     id="Definitions_1"
                     targetNamespace="http://bpmn.io/schema/bpmn">
        {rootElements}
        {process}
        </definitions>
        """;
}
