namespace Fleans.Domain.Activities;

public interface IStartProcessEventActivity: IActivity
{
    bool IsDefault { get; }
    string CorrelationKey { get; }
}