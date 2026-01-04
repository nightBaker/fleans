namespace Fleans.Domain.Errors;

[GenerateSerializer]
[Alias("Fleans.Domain.Errors.ActivityException")]
public abstract class ActivityException : Exception
{
    public abstract ActivityErrorState GetActivityErrorState();
}