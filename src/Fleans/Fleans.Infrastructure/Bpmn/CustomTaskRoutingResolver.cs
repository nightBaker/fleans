using System.Xml.Linq;

namespace Fleans.Infrastructure.Bpmn;

internal sealed class CustomTaskRoutingResolver
{
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
}
