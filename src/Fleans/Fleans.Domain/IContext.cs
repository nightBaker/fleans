namespace Fleans.Domain;

public interface IContext
{
    IActivity? CurrentActivity { get; }

    void EnqueueNextActivities(IEnumerable<IActivity> activities);
    bool GotoNextActivty();
}