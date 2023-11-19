namespace Fleans.Domain.Exceptions;

public class CurrentActivityNotCompletedException : InvalidOperationException
{
    public CurrentActivityNotCompletedException()
    {
    }

    public CurrentActivityNotCompletedException(string message)
        : base(message)
    {
    }

    public CurrentActivityNotCompletedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}