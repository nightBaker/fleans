[GenerateSerializer]
public record ConditionalStartEntry(
    [property: Id(0)] string ProcessDefinitionKey,
    [property: Id(1)] string ActivityId,
    [property: Id(2)] string ConditionExpression);
