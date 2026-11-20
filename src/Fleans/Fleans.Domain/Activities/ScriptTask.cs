using System.Dynamic;
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

    /// <summary>Whether the script runs built in (C#) rather than on an external worker plugin.</summary>
    public bool IsBuiltIn => ScriptFormats.IsBuiltIn(ScriptFormat);

    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);

        var variablesId = await activityContext.GetVariablesStateId();
        var workflowInstanceId = await workflowContext.GetWorkflowInstanceId();
        var activityInstanceId = await activityContext.GetActivityInstanceId();

        if (IsBuiltIn)
        {
            await activityContext.PublishEvent(new ExecuteScriptEvent(
                workflowInstanceId,
                definition.WorkflowId,
                definition.ProcessDefinitionId,
                activityInstanceId,
                ActivityId,
                Script,
                ScriptFormat,
                variablesId));
            return commands;
        }

        // Any other format runs on the worker plugin of fleans.script.<format> (#1105), through the
        // custom-task path: the script text is the reserved "script" input, the variables are the
        // ones the worker registered for (all of them).
        var inputs = new ExpandoObject();
        ((IDictionary<string, object?>)inputs)[ScriptFormats.ScriptInput] = Script;
        await activityContext.PublishEvent(new ExecuteCustomTaskEvent(
            workflowInstanceId,
            definition.WorkflowId,
            definition.ProcessDefinitionId,
            activityInstanceId,
            ActivityId,
            ScriptFormats.ToTaskType(ScriptFormat),
            inputs,
            variablesId,
            await workflowContext.GetBusinessId()));

        return commands;
    }
}
