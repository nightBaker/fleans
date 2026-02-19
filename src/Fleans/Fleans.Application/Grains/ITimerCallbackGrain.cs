namespace Fleans.Application.Grains;

public interface ITimerCallbackGrain : IGrainWithGuidCompoundKey
{
    Task Activate(TimeSpan dueTime);
    Task Cancel();
}
