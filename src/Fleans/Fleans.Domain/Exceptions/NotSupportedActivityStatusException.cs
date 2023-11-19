namespace Fleans.Domain.Exceptions;

public class NotSupportedActivityStatusException : NotSupportedException
{
    public NotSupportedActivityStatusException()
    {
    }

    public NotSupportedActivityStatusException(string message)
        : base(message)
    {
    }

    public NotSupportedActivityStatusException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}