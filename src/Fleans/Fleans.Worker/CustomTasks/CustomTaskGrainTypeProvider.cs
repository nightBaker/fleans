using Fleans.Application.Abstractions.Events;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Worker.CustomTasks;

/// <summary>
/// Names every <see cref="CustomTaskHandlerBase"/> subclass's grain type after its task type:
/// <c>custom-task.&lt;taskType&gt;</c>, read from the handler's
/// <c>[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.&lt;taskType&gt;")]</c>.
///
/// By default Orleans names a grain type after the bare class name (no namespace), so two plugin
/// hosts whose handlers share a class name advertised the same grain type: each host looked
/// compatible for the other's events, the wrong handler dropped the event, and the instance hung
/// on the task. The task type identifies the plugin, so a grain type built from it does not
/// collide. An explicit <c>[GrainType]</c> on a handler still wins (Orleans asks its attribute
/// provider first).
/// </summary>
internal sealed class CustomTaskGrainTypeProvider : IGrainTypeProvider
{
    private const string NamespacePrefix = WorkflowEventStreams.ExecuteCustomTaskStreamNamespace + ".";

    public bool TryGetGrainType(Type type, out GrainType grainType)
    {
        grainType = default;
        if (type.IsAbstract || !type.IsSubclassOf(typeof(CustomTaskHandlerBase)))
            return false;

        var taskType = SubscribedNamespaces(type)
            .Where(ns => ns.StartsWith(NamespacePrefix, StringComparison.Ordinal))
            .Select(ns => ns[NamespacePrefix.Length..])
            .FirstOrDefault();
        if (string.IsNullOrEmpty(taskType))
            return false;

        grainType = GrainType.Create(GetGrainType(taskType));
        return true;
    }

    /// <summary>The grain type of the handler for <paramref name="taskType"/>.</summary>
    internal static string GetGrainType(string taskType) => $"custom-task.{taskType}";

    /// <summary>
    /// The literal namespaces of <paramref name="type"/>'s <c>[ImplicitStreamSubscription]</c>
    /// attributes. The literal-string constructor keeps its argument in a private predicate, so
    /// read it from <see cref="System.Reflection.CustomAttributeData"/> — what Orleans itself reads
    /// during grain-class discovery. Concrete class only, as Orleans does.
    /// </summary>
    internal static IEnumerable<string> SubscribedNamespaces(Type type) =>
        type.GetCustomAttributesData()
            .Where(a => a.AttributeType == typeof(ImplicitStreamSubscriptionAttribute))
            .Select(a => a.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value as string : null)
            .OfType<string>();
}
