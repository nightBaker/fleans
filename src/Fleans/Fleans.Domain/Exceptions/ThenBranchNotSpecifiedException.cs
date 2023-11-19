namespace Fleans.Domain.Exceptions;

public class ThenBranchNotSpecifiedException : InvalidOperationException
{
    public ThenBranchNotSpecifiedException()
    {
    }

    public ThenBranchNotSpecifiedException(string message)
        : base(message)
    {
    }

    public ThenBranchNotSpecifiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}