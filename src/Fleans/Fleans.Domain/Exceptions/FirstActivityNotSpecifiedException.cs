namespace Fleans.Domain.Exceptions;

public class FirstActivityNotSpecifiedException : InvalidOperationException
{
    public FirstActivityNotSpecifiedException()
    {
    }

    public FirstActivityNotSpecifiedException(string message)
        : base(message)
    {
    }

    public FirstActivityNotSpecifiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}