using Fleans.Domain.Events;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ScriptTask : TaskActivity
{
    [Id(1)]
    public string Script { get; init; }

    [Id(2)]
    public string ScriptFormat { get; init; }

    public ScriptTask(string ActivityId, string Script, string ScriptFormat = "csharp") : base(ActivityId)
    {
        ArgumentNullException.ThrowIfNull(Script);
        this.Script = Script;
        this.ScriptFormat = ScriptFormat;
    }

    internal override async Task<IReadOnlyList<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = (await base.ExecuteAsync(workflowContext, activityContext, definition)).ToList();

        await activityContext.PublishEvent(new ExecuteScriptEvent(
            await workflowContext.GetWorkflowInstanceId(),
            definition.WorkflowId,
            definition.ProcessDefinitionId,
            await activityContext.GetActivityInstanceId(),
            ActivityId,
            Script,
            ScriptFormat));

        return commands;
    }
}
