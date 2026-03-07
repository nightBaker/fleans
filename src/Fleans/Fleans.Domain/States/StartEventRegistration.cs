namespace Fleans.Domain.States;

[GenerateSerializer]
public record StartEventRegistration(
    [property: Id(0)] string EventName,
    [property: Id(1)] string ProcessDefinitionKey);
