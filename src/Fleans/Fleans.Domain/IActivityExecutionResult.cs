namespace Fleans.Domain;

public interface IActivityExecutionResult
{
    ActivityResultStatus ActivityResultStatus { get; }
}

public record ActivityExecutionResult : IActivityExecutionResult
{
    public ActivityExecutionResult(ActivityResultStatus activityResultStatus)
    {
        ActivityResultStatus = activityResultStatus;
    }

    public ActivityResultStatus ActivityResultStatus { get; }
}

