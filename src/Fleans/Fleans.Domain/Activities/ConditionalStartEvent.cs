using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fleans.Domain.Tests")]

namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ConditionalStartEvent(
    string ActivityId,
    [property: Id(1)] string ConditionExpression) : StartEvent(ActivityId);
