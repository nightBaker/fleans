namespace Fleans.Domain;

[GenerateSerializer]
public record ActivityErrorState(int Code, string Message)
{
    private ActivityErrorState() : this(default, default!) { }
}
