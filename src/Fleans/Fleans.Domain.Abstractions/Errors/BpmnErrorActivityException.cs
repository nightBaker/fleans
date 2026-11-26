namespace Fleans.Domain.Errors;

/// <summary>A BPMN error thrown by the model itself, such as an error end event (#843).</summary>
[GenerateSerializer]
[Alias("Fleans.Domain.Errors.BpmnErrorActivityException")]
public class BpmnErrorActivityException : ActivityException
{
    [Id(0)]
    private readonly string _code;

    [Id(1)]
    private readonly string _message;

    public BpmnErrorActivityException(string code, string message)
    {
        _code = code;
        _message = message;
    }

    public override string Message => _message;

    public override ActivityErrorState GetActivityErrorState() => new(_code, _message);
}
