namespace Fleans.Domain
{
    public record ActivityResult<T>(T Result)
    {
        public DateTime CreatedAt { get; } = DateTime.Now;
    }
}