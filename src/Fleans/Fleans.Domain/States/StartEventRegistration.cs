namespace Fleans.Domain.States;

[GenerateSerializer]
public record MessageStartEventRegistration(
    [property: Id(0)] string MessageName,
    [property: Id(1)] string ProcessDefinitionKey);

[GenerateSerializer]
public record SignalStartEventRegistration(
    [property: Id(0)] string SignalName,
    [property: Id(1)] string ProcessDefinitionKey);
