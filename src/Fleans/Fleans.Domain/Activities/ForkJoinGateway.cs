namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record ForkJoinGateway(
    string ActivityId,
    bool IsFork) : ConditionalGateway(ActivityId)
{
    internal override bool IsJoinGateway => !IsFork;

    protected async Task<bool> AllExpectedTokensArrived(
        IWorkflowExecutionContext workflowContext, IWorkflowDefinition definition,
        IActivityExecutionContext? activityContext = null)
    {
        var incomingFlows = definition.GetIncomingFlows(this).ToList();
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

        // The join entry carries the token of the first branch that reached it, so its own fork is
        // the one this join closes. Another live copy of the same fork/join — a parallel
        // multi-instance iteration — has its own fork, which must not complete this join (#839).
        if (activityContext is not null && await activityContext.GetTokenId() is { } ownToken
            && await workflowContext.FindForkByToken(ownToken) is { } ownFork)
            return ownFork.CreatedTokenIds.All(arrivedTokens.Contains);

        // Tokens of an earlier loop iteration have arrived too, but their fork is gone.
        foreach (var token in arrivedTokens)
        {
            var forkState = await workflowContext.FindForkByToken(token);
            if (forkState != null && forkState.CreatedTokenIds.All(t => arrivedTokens.Contains(t)))
                return true;
        }

        return false;
    }
}
