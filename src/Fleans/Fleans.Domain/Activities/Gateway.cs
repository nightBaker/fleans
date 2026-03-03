using Fleans.Domain.States;

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Gateway(string ActivityId) : Activity(ActivityId)
{
    internal virtual bool CreatesNewTokensOnFork => false;
    internal virtual bool ClonesVariablesOnFork => false;
    internal virtual Guid? GetRestoredTokenAfterJoin(GatewayForkState? forkState) => null;
}
