using System.Dynamic;
using Fleans.Domain.Events;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record CustomTaskActivity : TaskActivity
{
    [Id(1)]
    public string TaskType { get; init; }

    public CustomTaskActivity(string ActivityId, string TaskType) : base(ActivityId)
    {
        ArgumentNullException.ThrowIfNull(TaskType);
        this.TaskType = TaskType;
    }

    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        // Inputs were evaluated into the activity's local scope before it executed; the plugin
        // receives their values.
        var variablesId = await activityContext.GetVariablesStateId();
        var inputs = new ExpandoObject();
        var inputsDict = (IDictionary<string, object?>)inputs;
        foreach (var input in InputMappings)
            inputsDict[input.Target] = await workflowContext.GetVariable(variablesId, input.Target);

        await activityContext.PublishEvent(new ExecuteCustomTaskEvent(
            await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            definition.ProcessDefinitionId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            TaskType,
            inputs,
            variablesId,
            await workflowContext.GetBusinessId()));

        return commands;
    }
}
