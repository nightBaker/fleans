using System.Xml.Linq;
using Fleans.Domain.Activities;

namespace Fleans.Infrastructure.Bpmn;

internal sealed class CustomTaskRoutingResolver
{
    private static readonly System.Text.RegularExpressions.Regex IdentifierRegex =
        new("^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public string? ResolveTaskType(XElement serviceTask)
    {
        var attr = serviceTask.Attribute("type")?.Value;
        if (!string.IsNullOrWhiteSpace(attr))
            return attr;

        var extensions = serviceTask.Element(BpmnNamespaces.Bpmn + "extensionElements");
        if (extensions is null) return null;

        var taskDef = BpmnNamespaces.FindExtensionElement(extensions, "taskDefinition");
        var taskType = taskDef?.Attribute("type")?.Value;
        if (!string.IsNullOrWhiteSpace(taskType))
            return taskType;

        return null;
    }

    public List<InputMapping> ParseInputMappings(XElement serviceTask)
    {
        var mappings = new List<InputMapping>();
        var extensions = serviceTask.Element(BpmnNamespaces.Bpmn + "extensionElements");
        if (extensions is null) return mappings;

        var ioMapping = BpmnNamespaces.FindExtensionElement(extensions, "ioMapping");
        if (ioMapping is null) return mappings;

        foreach (var input in BpmnNamespaces.FindExtensionElements(ioMapping, "input"))
            mappings.Add(ParseInputMapping(input));

        return mappings;
    }

    public List<OutputMapping> ParseOutputMappings(XElement serviceTask)
    {
        var mappings = new List<OutputMapping>();
        var extensions = serviceTask.Element(BpmnNamespaces.Bpmn + "extensionElements");
        if (extensions is null) return mappings;

        var ioMapping = BpmnNamespaces.FindExtensionElement(extensions, "ioMapping");
        if (ioMapping is null) return mappings;

        foreach (var output in BpmnNamespaces.FindExtensionElements(ioMapping, "output"))
            mappings.Add(ParseOutputMapping(output));

        return mappings;
    }

    private static InputMapping ParseInputMapping(XElement input)
    {
        var source = input.Attribute("source")?.Value
            ?? throw new InvalidOperationException("<input> missing required 'source' attribute");
        var target = input.Attribute("target")?.Value
            ?? throw new InvalidOperationException("<input> missing required 'target' attribute");

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("<input> 'target' is empty or whitespace-only");

        if (!IdentifierRegex.IsMatch(target))
            throw new InvalidOperationException(
                $"<input target=\"{target}\"> target must be a valid identifier (^[a-zA-Z_][a-zA-Z0-9_]*$)");

        ValidateMappingSource(source, $"<input target=\"{target}\">");

        return new InputMapping(source, target);
    }

    private static OutputMapping ParseOutputMapping(XElement output)
    {
        var source = output.Attribute("source")?.Value
            ?? throw new InvalidOperationException("<output> missing required 'source' attribute");
        var target = output.Attribute("target")?.Value
            ?? throw new InvalidOperationException("<output> missing required 'target' attribute");

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("<output> 'target' is empty or whitespace-only");

        if (!IdentifierRegex.IsMatch(target))
            throw new InvalidOperationException(
                $"<output target=\"{target}\"> target must be a valid identifier (^[a-zA-Z_][a-zA-Z0-9_]*$)");

        if (target == "__response")
            throw new InvalidOperationException(
                "<output target=\"__response\"> is reserved — providers populate it during execution; output mapping cannot target it directly");

        ValidateMappingSource(source, $"<output target=\"{target}\">");

        return new OutputMapping(source, target);
    }

    private static void ValidateMappingSource(string source, string errorPrefix)
    {
        if (string.IsNullOrEmpty(source))
            throw new InvalidOperationException($"{errorPrefix} has empty 'source' attribute");

        if (source[0] != '=')
            return;

        var expr = source.Substring(1);
        if (expr.Length == 0)
            throw new InvalidOperationException($"{errorPrefix} 'source' is bare '=' with no expression");

        if (expr[0] == '"')
        {
            if (expr.Length < 2 || expr[expr.Length - 1] != '"')
                throw new InvalidOperationException($"{errorPrefix} has unmatched quote in source: '{source}'");
            return;
        }

        if (expr == "true" || expr == "false" || expr == "null") return;
        if (long.TryParse(expr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _)) return;
        if (double.TryParse(expr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return;

        var segments = expr.Split('.');
        foreach (var seg in segments)
        {
            if (!IdentifierRegex.IsMatch(seg))
                throw new InvalidOperationException(
                    $"{errorPrefix} has invalid path segment '{seg}' in source: '{source}'");
        }
    }
}
