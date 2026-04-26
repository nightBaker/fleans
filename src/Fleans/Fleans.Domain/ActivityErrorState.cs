namespace Fleans.Domain;

[GenerateSerializer]
public record ActivityErrorState(string Code, string Message)
{
    private ActivityErrorState() : this(default!, default!) { }
}
