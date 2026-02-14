namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract class Gateway : Activity
{
    protected Gateway(string ActivityId) : base(ActivityId)
    {
    }
}
