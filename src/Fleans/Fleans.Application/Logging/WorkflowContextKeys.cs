namespace Fleans.Application.Logging;

/// <summary>
/// Constants for Orleans RequestContext keys used to propagate
/// workflow correlation identifiers across grain calls.
/// </summary>
internal static class WorkflowContextKeys
{
    public const string WorkflowId = "WorkflowId";
    public const string ProcessDefinitionId = "ProcessDefinitionId";
    public const string WorkflowInstanceId = "WorkflowInstanceId";
    public const string ActivityId = "ActivityId";
    public const string ActivityInstanceId = "ActivityInstanceId";
    public const string VariablesId = "VariablesId";
}
