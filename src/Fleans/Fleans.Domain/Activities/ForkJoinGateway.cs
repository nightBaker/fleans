namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record ForkJoinGateway(
    string ActivityId,
    bool IsFork) : ConditionalGateway(ActivityId)
{
    internal override bool IsJoinGateway => !IsFork;

    protected async Task<bool> AllExpectedTokensArrived(
        IWorkflowExecutionContext workflowContext, IWorkflowDefinition definition)
    {
        var incomingFlows = definition.SequenceFlows.Where(sf => sf.Target == this).ToList();
        var completedActivities = await workflowContext.GetCompletedActivities();

        var arrivedTokens = new HashSet<Guid>();
        foreach (var flow in incomingFlows)
        {
            foreach (var completed in completedActivities)
            {
                if (await completed.GetActivityId() == flow.Source.ActivityId)
                {
                    var tokenId = await completed.GetTokenId();
                    if (tokenId.HasValue)
                        arrivedTokens.Add(tokenId.Value);
                }
            }
        }

        if (arrivedTokens.Count == 0)
            return false;

        var forkState = await workflowContext.FindForkByToken(arrivedTokens.First());
        if (forkState == null)
            return false;

        return forkState.CreatedTokenIds.All(t => arrivedTokens.Contains(t));
    }
}
