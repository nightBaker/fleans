using System.Xml.Linq;
using Fleans.Domain;

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

    /// <summary>
    /// The plugin version in <c>taskDefinition</c>; <c>null</c> when absent or <c>latest</c>. Throws on
    /// anything else that isn't semver, so the deploy fails.
    /// </summary>
    public string? ResolveTaskVersion(XElement serviceTask, string activityId)
    {
        var extensions = serviceTask.Element(BpmnNamespaces.Bpmn + "extensionElements");
        var version = extensions is null
            ? null
            : BpmnNamespaces.FindExtensionElement(extensions, "taskDefinition")?.Attribute("version")?.Value;
        if (version is null || version == CustomTaskVersions.Latest)
            return null;

        return CustomTaskVersions.TryNormalize(version, out var normalized)
            ? normalized
            : throw new InvalidOperationException(
                $"Service task '{activityId}': version '{version}' must be '{CustomTaskVersions.Latest}' or a semantic version such as 1.4.0.");
    }
}
