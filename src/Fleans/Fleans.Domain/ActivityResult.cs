namespace Fleans.Domain
{
    public record ActivityResult<T>(T result)
    {
        public DateTime CreatedAt { get; } = DateTime.Now;
    }
}