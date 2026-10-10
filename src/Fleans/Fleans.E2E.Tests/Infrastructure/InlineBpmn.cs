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

    /// <summary>start → script → end. Completes immediately when started.</summary>
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
    /// start → message catch (correlated on the <c>requestId</c> start variable) → end.
    /// Instances stay Running until the message is delivered with the matching key.
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

    private static string Wrap(string rootElements, string process) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                     xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
                     id="Definitions_1"
                     targetNamespace="http://bpmn.io/schema/bpmn">
        {rootElements}
        {process}
        </definitions>
        """;
}
