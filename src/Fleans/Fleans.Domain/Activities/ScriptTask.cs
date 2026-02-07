using System.Runtime.CompilerServices;
using Fleans.Domain.Events;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ScriptTask : TaskActivity
{
    [Id(0)]
    public string Script { get; init; }

    [Id(1)]
    public string ScriptFormat { get; init; }

    public ScriptTask(string ActivityId, string Script, string ScriptFormat = "csharp") : base(ActivityId)
    {
        this.Script = Script;
        this.ScriptFormat = ScriptFormat;
    }

    internal override async Task ExecuteAsync(IWorkflowInstance workflowInstance, IActivityInstance activityInstance)
    {
        await base.ExecuteAsync(workflowInstance, activityInstance);

        var definition = await workflowInstance.GetWorkflowDefinition();
        await activityInstance.PublishEvent(new ExecuteScriptEvent(
            workflowInstance.GetGrainId().GetGuidKey(),
            definition.WorkflowId,
            await activityInstance.GetActivityInstanceId(),
            ActivityId,
            Script));
    }
}
