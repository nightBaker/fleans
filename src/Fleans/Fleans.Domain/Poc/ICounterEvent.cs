namespace Fleans.Domain.Poc;

public interface ICounterEvent;

[GenerateSerializer]
public record CounterIncremented([property: Id(0)] int Amount) : ICounterEvent;

[GenerateSerializer]
public record CounterDecremented([property: Id(0)] int Amount) : ICounterEvent;

[GenerateSerializer]
public record CounterReset() : ICounterEvent;
