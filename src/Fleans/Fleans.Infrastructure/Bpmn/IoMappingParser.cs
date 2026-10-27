using System.Text.RegularExpressions;
using System.Xml.Linq;
using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Bpmn;

/// <summary>
/// Reads <c>&lt;fleans:ioMapping&gt;</c> (or <c>zeebe:ioMapping</c>) from an element's
/// <c>extensionElements</c>. Sources are evaluated at run time, so only their shape is checked here;
/// targets must be plain identifiers. Inputs and outputs are accepted on the elements where
/// Camunda 8 accepts <c>zeebe:ioMapping</c>.
/// </summary>
internal static partial class IoMappingParser
{
    private static readonly HashSet<string> Tasks = ["task", "userTask", "serviceTask", "scriptTask"];
    private static readonly HashSet<string> InputElements = [.. Tasks, "subProcess", "transaction", "callActivity", "intermediateThrowEvent", "endEvent"];
    private static readonly HashSet<string> OutputElements = [.. Tasks, "subProcess", "transaction", "callActivity", "intermediateCatchEvent", "boundaryEvent", "startEvent"];

    public static (List<InputMapping> Inputs, List<OutputMapping> Outputs) Parse(XElement element)
    {
        var inputs = new List<InputMapping>();
        var outputs = new List<OutputMapping>();

        var extensions = element.Element(BpmnNamespaces.Bpmn + "extensionElements");
        var ioMapping = extensions is null ? null : BpmnNamespaces.FindExtensionElement(extensions, "ioMapping");
        if (ioMapping is null)
            return (inputs, outputs);

        foreach (var input in BpmnNamespaces.FindExtensionElements(ioMapping, "input"))
        {
            var (source, target) = ParseMapping(input, "input");
            inputs.Add(new InputMapping(source, target));
        }
        foreach (var output in BpmnNamespaces.FindExtensionElements(ioMapping, "output"))
        {
            var (source, target) = ParseMapping(output, "output");
            outputs.Add(new OutputMapping(source, target));
        }

        var elementName = element.Name.LocalName;
        var id = element.Attribute("id")?.Value;
        if (element.Attribute("triggeredByEvent")?.Value == "true")
            elementName = "event sub-process";
        if (inputs.Count > 0 && !InputElements.Contains(elementName))
            throw new InvalidOperationException(
                $"<input> mappings are not supported on {elementName} '{id}'. Inputs apply to tasks, sub-processes, " +
                "call activities, intermediate throw events and end events.");
        if (outputs.Count > 0 && !OutputElements.Contains(elementName))
            throw new InvalidOperationException(
                $"<output> mappings are not supported on {elementName} '{id}'. Outputs apply to tasks, sub-processes, " +
                "call activities, intermediate catch events, boundary events and start events.");

        return (inputs, outputs);
    }

    private static (string Source, string Target) ParseMapping(XElement mapping, string kind)
    {
        var source = mapping.Attribute("source")?.Value
            ?? throw new InvalidOperationException($"<{kind}> missing required 'source' attribute");
        var target = mapping.Attribute("target")?.Value
            ?? throw new InvalidOperationException($"<{kind}> missing required 'target' attribute");

        if (!IdentifierRegex().IsMatch(target))
            throw new InvalidOperationException(
                $"<{kind} target=\"{target}\"> target must be a valid identifier (^[a-zA-Z_][a-zA-Z0-9_]*$)");
        if (source.Length == 0)
            throw new InvalidOperationException($"<{kind} target=\"{target}\"> has empty 'source' attribute");
        if (source[0] == '=' && source[1..].Trim().Length == 0)
            throw new InvalidOperationException($"<{kind} target=\"{target}\"> 'source' is bare '=' with no expression");

        return (source, target);
    }

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex IdentifierRegex();
}
