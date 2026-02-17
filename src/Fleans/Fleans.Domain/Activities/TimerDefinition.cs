using System.Xml;

namespace Fleans.Domain.Activities;

public enum TimerType
{
    Duration,
    Date,
    Cycle
}

[GenerateSerializer]
public record TimerDefinition(
    [property: Id(0)] TimerType Type,
    [property: Id(1)] string Expression)
{
    public TimeSpan GetDueTime()
    {
        return Type switch
        {
            TimerType.Duration => XmlConvert.ToTimeSpan(Expression),
            TimerType.Date => GetDateDueTime(),
            TimerType.Cycle => ParseCycle().Interval,
            _ => throw new InvalidOperationException($"Unknown timer type: {Type}")
        };
    }

    private TimeSpan GetDateDueTime()
    {
        var targetDate = DateTimeOffset.Parse(Expression);
        var remaining = targetDate - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public (int? RepeatCount, TimeSpan Interval) ParseCycle()
    {
        var parts = Expression.Split('/');
        if (parts.Length != 2)
            throw new InvalidOperationException($"Invalid cycle expression: {Expression}. Expected format: R{{count}}/{{duration}}");

        var repeatPart = parts[0];
        int? repeatCount = repeatPart.Length > 1
            ? int.Parse(repeatPart[1..])
            : null;

        var interval = XmlConvert.ToTimeSpan(parts[1]);
        return (repeatCount, interval);
    }
}
