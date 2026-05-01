using Fleans.Domain.Events;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record CustomTaskActivity : TaskActivity
{
    [Id(1)]
    public string TaskType { get; init; }

    [Id(2)]
    public List<InputMapping> InputMappings { get; init; }

    [Id(3)]
    public List<OutputMapping> OutputMappings { get; init; }

    public CustomTaskActivity(
        string ActivityId,
        string TaskType,
        IEnumerable<InputMapping>? InputMappings,
        IEnumerable<OutputMapping>? OutputMappings) : base(ActivityId)
    {
        ArgumentNullException.ThrowIfNull(TaskType);
        this.TaskType = TaskType;
        this.InputMappings = InputMappings is List<InputMapping> il ? il : InputMappings?.ToList() ?? new List<InputMapping>();
        this.OutputMappings = OutputMappings is List<OutputMapping> ol ? ol : OutputMappings?.ToList() ?? new List<OutputMapping>();
    }

    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        var variablesId = await activityContext.GetVariablesStateId();
        await activityContext.PublishEvent(new ExecuteCustomTaskEvent(
            await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            definition.ProcessDefinitionId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            TaskType,
            InputMappings,
            OutputMappings,
            variablesId));

        return commands;
    }
}
