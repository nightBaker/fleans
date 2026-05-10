using System.Xml.Linq;

namespace Fleans.Infrastructure.Bpmn;

internal static class BpmnNamespaces
{
    public static readonly XNamespace Bpmn    = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    public static readonly XNamespace Bpmndi  = "http://www.omg.org/spec/BPMN/20100524/DI";
    public static readonly XNamespace Zeebe   = "http://camunda.org/schema/zeebe/1.0";
    public static readonly XNamespace Fleans  = "https://fleans.io/schema/bpmn/1.0";
    // Accepted on read for backward compatibility with BPMN authored before the URI version bump.
    // Remove once we are confident no in-the-wild files still carry it.
    public static readonly XNamespace FleansLegacy = "http://fleans.io/schema/bpmn/fleans";
    public static readonly XNamespace Camunda = "http://camunda.org/schema/1.0/bpmn";

    public static readonly IReadOnlyList<XNamespace> ExtensionNamespaces = [Fleans, FleansLegacy, Zeebe];

    public static XElement? FindExtensionElement(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e =>
            e.Name.LocalName == localName &&
            (e.Name.Namespace == Fleans || e.Name.Namespace == FleansLegacy || e.Name.Namespace == Zeebe));

    public static IEnumerable<XElement> FindExtensionElements(XElement parent, string localName)
        => parent.Elements().Where(e =>
            e.Name.LocalName == localName &&
            (e.Name.Namespace == Fleans || e.Name.Namespace == FleansLegacy || e.Name.Namespace == Zeebe));

    public static string? GetExtensionAttributeValue(XElement element, string localName)
        => element.Attribute(Fleans + localName)?.Value
           ?? element.Attribute(FleansLegacy + localName)?.Value
           ?? element.Attribute(Zeebe + localName)?.Value;
}
