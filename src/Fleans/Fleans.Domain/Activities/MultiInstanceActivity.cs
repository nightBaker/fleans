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
        // MI iteration: delegate directly to the inner activity (no boundary registration)
        var miIndex = await activityContext.GetMultiInstanceIndex();
        if (miIndex is not null)
            return await InnerActivity.ExecuteAsync(workflowContext, activityContext, definition);

        // MI host: register boundaries and spawn iterations
        var commands = await base.ExecuteAsync(workflowContext, activityContext, definition);
        var variablesId = await activityContext.GetVariablesStateId();
        var hostInstanceId = await activityContext.GetActivityInstanceId();

        // Resolve iteration count and collection items
        int count;
        IList<object>? collectionItems = null;

        if (LoopCardinality.HasValue)
        {
            count = LoopCardinality.Value;
        }
        else if (InputCollection is not null)
        {
            var collectionVar = await workflowContext.GetVariable(variablesId, InputCollection);
            if (collectionVar is IList<object> list)
            {
                collectionItems = list;
                count = list.Count;
            }
            else if (collectionVar is System.Collections.IEnumerable enumerable and not string)
            {
                collectionItems = enumerable.Cast<object>().ToList();
                count = collectionItems.Count;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Multi-instance inputCollection '{InputCollection}' must resolve to a list/array, got: {collectionVar?.GetType().Name ?? "null"}");
            }
        }
        else
        {
            throw new InvalidOperationException(
                "Multi-instance must have either LoopCardinality or InputCollection");
        }

        // For empty collection — complete the host immediately
        if (count == 0)
        {
            await activityContext.Complete();
            return commands;
        }

        // Spawn iteration commands
        var iterationsToSpawn = IsSequential ? Math.Min(1, count) : count;
        for (var i = 0; i < iterationsToSpawn; i++)
        {
            commands.Add(new SpawnActivityCommand(InnerActivity, hostInstanceId, hostInstanceId)
            {
                MultiInstanceIndex = i,
                MultiInstanceTotal = count,
                ParentVariablesId = variablesId,
                IterationItem = collectionItems is not null && InputDataItem is not null ? collectionItems[i] : null,
                IterationItemName = InputDataItem
            });
        }

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
