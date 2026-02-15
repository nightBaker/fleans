namespace Fleans.Domain.Activities;

[GenerateSerializer]
public abstract record Gateway(string ActivityId) : Activity(ActivityId);
