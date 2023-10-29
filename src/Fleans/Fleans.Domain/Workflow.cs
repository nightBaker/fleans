namespace Fleans.Domain;

public record Workflow
{
    private readonly WorkflowContext _context;

    public Workflow(Guid id,
                    int version,
                    Dictionary<string, object> initialContext,
                    IActivity firstActivity)
    {
        Id = id;
        Version = version;
        _context = new WorkflowContext(initialContext);
        CurrentActivity = firstActivity;
    }

    public Guid Id { get; }
    public int Version { get; }
    
    public IActivity CurrentActivity { get; private set; }
    public IReadOnlyDictionary<string, object> Context => _context.Context;

    public async Task Run()
    {
        await CurrentActivity.ExecuteAsync(_context);
        
        var nextActivities = CurrentActivity.GetNextActivites(_context);

        while (nextActivities.Any())
        {
            var nextNextActivities = new List<IActivity>();
            foreach (var activity in nextActivities)
            {
                await activity.ExecuteAsync(_context);
                nextNextActivities.AddRange(activity.GetNextActivites(_context));
                CurrentActivity = activity;
            }

            nextActivities = nextNextActivities.ToArray();
        }
    }
}