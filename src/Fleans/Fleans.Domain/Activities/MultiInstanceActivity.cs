using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record MultiInstanceActivity : BoundarableActivity
{
    [Id(1)] public Activity InnerActivity { get; init; }
    [Id(2)] public bool IsSequential { get; init; }
    [Id(3)] public int? LoopCardinality { get; init; }
    [Id(4)] public string? InputCollection { get; init; }
    [Id(5)] public string? InputDataItem { get; init; }
    [Id(6)] public string? OutputCollection { get; init; }
    [Id(7)] public string? OutputDataItem { get; init; }

    public MultiInstanceActivity(
        string ActivityId,
        Activity InnerActivity,
        bool IsSequential = false,
        int? LoopCardinality = null,
        string? InputCollection = null,
        string? InputDataItem = null,
        string? OutputCollection = null,
        string? OutputDataItem = null) : base(ActivityId)
    {
        if (LoopCardinality is null && InputCollection is null)
            throw new ArgumentException("MultiInstanceActivity must have either LoopCardinality or InputCollection");
        if (LoopCardinality.HasValue && LoopCardinality.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(LoopCardinality), "LoopCardinality must be non-negative");

        this.InnerActivity = InnerActivity;
        this.IsSequential = IsSequential;
        this.LoopCardinality = LoopCardinality;
        this.InputCollection = InputCollection;
        this.InputDataItem = InputDataItem;
        this.OutputCollection = OutputCollection;
        this.OutputDataItem = OutputDataItem;
    }

    internal override async Task<List<IExecutionCommand>> ExecuteAsync(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var variablesId = await activityContext.GetVariablesStateId();
        commands.Add(new OpenMultiInstanceCommand(this, variablesId));
        return commands;
    }

    internal override Task<List<Activity>> GetNextActivities(
        IWorkflowExecutionContext workflowContext,
        IActivityExecutionContext activityContext,
        IWorkflowDefinition definition)
    {
        var nextFlows = definition.SequenceFlows
            .Where(sf => sf.Source == this)
            .Select(flow => flow.Target)
            .ToList();
        return Task.FromResult(nextFlows);
    }
}
