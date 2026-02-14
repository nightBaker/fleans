using Fleans.Domain.Events;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public class ScriptTask : TaskActivity
{
    [Id(0)]
    public string Script { get; set; }

    [Id(1)]
    public string ScriptFormat { get; set; }

    public ScriptTask(string ActivityId, string Script, string ScriptFormat = "csharp") : base(ActivityId)
    {
        ArgumentNullException.ThrowIfNull(Script);
        this.Script = Script;
        this.ScriptFormat = ScriptFormat;
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        await base.ExecuteAsync(workflowInstance, activityInstance);

        var definition = await workflowInstance.GetWorkflowDefinition();
        await activityInstance.PublishEvent(new ExecuteScriptEvent(
            await workflowInstance.GetWorkflowInstanceId(),
            definition.WorkflowId,
            definition.ProcessDefinitionId,
            await activityInstance.GetActivityInstanceId(),
            ActivityId,
            Script,
            ScriptFormat));
    }
}
