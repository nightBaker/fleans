namespace Fleans.Domain.Poc;

public interface IJournaledCounterGrain : IGrainWithStringKey
{
    Task Increment(int amount);
    Task Decrement(int amount);
    Task Reset();
    Task<int> GetValue();
    Task<int> GetVersion();
    Task<int> GetEventCount();
    Task Deactivate();
}
