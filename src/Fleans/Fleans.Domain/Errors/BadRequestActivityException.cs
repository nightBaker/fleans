namespace Fleans.Domain.Errors;

[GenerateSerializer]
[Alias("Fleans.Domain.Errors.BadRequestActivityException")]
public class BadRequestActivityException(string message) : ActivityException
{
    [Id(0)]
    private readonly string _message = message;

    public override ActivityErrorState GetActivityErrorState()
    {
        return new ActivityErrorState(400, _message);
    }
}