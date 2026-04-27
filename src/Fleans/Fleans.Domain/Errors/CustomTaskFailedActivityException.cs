namespace Fleans.Domain.Errors;

[GenerateSerializer]
[Alias("Fleans.Domain.Errors.CustomTaskFailedActivityException")]
public class CustomTaskFailedActivityException : ActivityException
{
    [Id(0)]
    private readonly int _code;

    [Id(1)]
    private readonly string _message;

    public CustomTaskFailedActivityException(int code, string message)
    {
        _code = code;
        _message = message;
    }

    public override ActivityErrorState GetActivityErrorState()
    {
        return new ActivityErrorState(_code, _message);
    }
}
