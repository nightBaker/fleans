namespace Fleans.Domain.Activities;

[GenerateSerializer]
public record ActivityTransition(
    [property: Id(0)] Activity NextActivity,
    [property: Id(1)] bool CloneVariables = false,
    [property: Id(2)] TokenAction Token = TokenAction.Inherit);

public enum TokenAction
{
    Inherit,
    CreateNew,
    RestoreParent
}
