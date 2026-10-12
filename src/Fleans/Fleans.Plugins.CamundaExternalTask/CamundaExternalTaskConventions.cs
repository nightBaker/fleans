namespace Fleans.Plugins.CamundaExternalTask;

/// <summary>
/// BPMN contract: <c>&lt;serviceTask type="camunda:charge-card"&gt;</c> is a Camunda external task on
/// topic <c>charge-card</c>. It replaces Camunda 7's
/// <c>camunda:type="external" camunda:topic="charge-card"</c> attribute pair.
/// </summary>
public static class CamundaExternalTaskConventions
{
    public const string TaskTypePrefix = "camunda:";

    /// <summary>Matches <c>WorkflowEventStreams.GetExecuteCustomTaskNamespace("camunda:&lt;topic&gt;")</c>.</summary>
    public const string StreamNamespacePattern = @"^events\.ExecuteCustomTaskEvent\.camunda:.+$";

    public static string TaskTypeFor(string topic) => TaskTypePrefix + topic;

    public static string? TryGetTopic(string taskType) =>
        taskType.StartsWith(TaskTypePrefix, StringComparison.Ordinal) && taskType.Length > TaskTypePrefix.Length
            ? taskType[TaskTypePrefix.Length..]
            : null;
}
