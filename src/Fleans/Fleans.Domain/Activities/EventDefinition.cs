namespace Fleans.Domain.Activities;

/// <summary>
/// Composition container for Multiple Event definitions.
/// Used only inside Multiple*Event records — existing single-definition
/// event records are NOT refactored to use this hierarchy.
/// </summary>
[GenerateSerializer]
public abstract record EventDefinition;

[GenerateSerializer]
public record MessageEventDef(
    [property: Id(0)] string MessageDefinitionId) : EventDefinition;

[GenerateSerializer]
public record SignalEventDef(
    [property: Id(0)] string SignalDefinitionId) : EventDefinition;

[GenerateSerializer]
public record TimerEventDef(
    [property: Id(0)] TimerDefinition TimerDefinition) : EventDefinition;
