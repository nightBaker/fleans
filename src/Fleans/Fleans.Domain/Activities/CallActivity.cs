using System.Dynamic;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record CallActivity(
    string ActivityId,
    [property: Id(1)] string CalledProcessKey,
    [property: Id(2)] List<VariableMapping> InputMappings,
    [property: Id(3)] List<VariableMapping> OutputMappings,
    [property: Id(4)] bool PropagateAllParentVariables = true,
    [property: Id(5)] bool PropagateAllChildVariables = true) : BoundarableActivity(ActivityId)
{
    internal override async Task<List<IExecutionCommand>> ExecuteAsync(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        commands.Add(new StartChildWorkflowCommand(this));
        return commands;
    }

    internal override Task<List<ActivityTransition>> GetNextActivities(IWorkflowExecutionContext workflowContext, IActivityExecutionContext activityContext, IWorkflowDefinition definition)
    {
        var nextFlow = definition.GetOutgoingFlow(this);
        return Task.FromResult(nextFlow != null ? new List<ActivityTransition> { new(nextFlow.Target) } : new List<ActivityTransition>());
    }

    public ExpandoObject BuildChildInputVariables(ExpandoObject parentVariables)
    {
        var result = new ExpandoObject();
        var sourceDict = (IDictionary<string, object?>)parentVariables;
        var resultDict = (IDictionary<string, object?>)result;

        if (PropagateAllParentVariables)
        {
            foreach (var kvp in sourceDict)
                resultDict[kvp.Key] = kvp.Value;
        }

        foreach (var mapping in InputMappings)
        {
            if (sourceDict.TryGetValue(mapping.Source, out var value))
                resultDict[mapping.Target] = value;
        }

        return result;
    }

    public ExpandoObject BuildParentOutputVariables(ExpandoObject childVariables)
    {
        var result = new ExpandoObject();
        var sourceDict = (IDictionary<string, object?>)childVariables;
        var resultDict = (IDictionary<string, object?>)result;

        if (PropagateAllChildVariables)
        {
            foreach (var kvp in sourceDict)
                resultDict[kvp.Key] = kvp.Value;
        }

        foreach (var mapping in OutputMappings)
        {
            if (sourceDict.TryGetValue(mapping.Source, out var value))
                resultDict[mapping.Target] = value;
        }

        return result;
    }
}
