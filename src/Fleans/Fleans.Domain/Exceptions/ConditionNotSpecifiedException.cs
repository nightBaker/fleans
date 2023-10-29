namespace Fleans.Domain.Exceptions;

public class ConditionNotSpecifiedException : InvalidOperationException
{
    public ConditionNotSpecifiedException()
    {
    }

    public ConditionNotSpecifiedException(string message)
        : base(message)
    {
    }

    public ConditionNotSpecifiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}