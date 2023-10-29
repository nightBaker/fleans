namespace Fleans.Domain.Exceptions;

public class ActivityNotCompletedException : InvalidOperationException
{
    public ActivityNotCompletedException()
    {
    }

    public ActivityNotCompletedException(string message)
        : base(message)
    {
    }

    public ActivityNotCompletedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}