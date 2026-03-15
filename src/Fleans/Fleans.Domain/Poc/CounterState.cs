namespace Fleans.Domain.Poc;

[GenerateSerializer]
public class CounterState
{
    [Id(0)]
    public int Value { get; set; }

    [Id(1)]
    public int EventCount { get; set; }

    public void Apply(ICounterEvent @event)
    {
        switch (@event)
        {
            case CounterIncremented e:
                Value += e.Amount;
                break;
            case CounterDecremented e:
                Value -= e.Amount;
                break;
            case CounterReset:
                Value = 0;
                break;
        }

        EventCount++;
    }
}
