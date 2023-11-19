namespace Fleans.Domain;

public record ActivityResult<T>(T? Result)
{
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}

public record ErrorActivityResult<T>(Exception Error) : ActivityResult<T>(default(T))
{
    
}