namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Gateway : Activity
{
    protected Gateway(string ActivityId) : base(ActivityId)
    {
    }
}
