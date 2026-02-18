using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class TimerDefinitionTests
{
    [TestMethod]
    public void GetDueTime_Duration_ShouldReturnTimeSpan()
    {
        var timer = new TimerDefinition(TimerType.Duration, "PT5M");
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.FromMinutes(5), dueTime);
    }

    [TestMethod]
    public void GetDueTime_Date_ShouldReturnTimeUntilDate()
    {
        var futureDate = DateTimeOffset.UtcNow.AddHours(1);
        var timer = new TimerDefinition(TimerType.Date, futureDate.ToString("o"));
        var dueTime = timer.GetDueTime();
        Assert.IsTrue(dueTime > TimeSpan.FromMinutes(59));
        Assert.IsTrue(dueTime < TimeSpan.FromMinutes(61));
    }

    [TestMethod]
    public void GetDueTime_Date_InPast_ShouldReturnZero()
    {
        var pastDate = DateTimeOffset.UtcNow.AddHours(-1);
        var timer = new TimerDefinition(TimerType.Date, pastDate.ToString("o"));
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.Zero, dueTime);
    }

    [TestMethod]
    public void GetDueTime_Cycle_ShouldReturnInterval()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.FromMinutes(10), dueTime);
    }

    [TestMethod]
    public void ParseCycle_ShouldReturnRepeatCountAndInterval()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R3/PT10M");
        var (repeatCount, interval) = timer.ParseCycle();
        Assert.AreEqual(3, repeatCount);
        Assert.AreEqual(TimeSpan.FromMinutes(10), interval);
    }

    [TestMethod]
    public void ParseCycle_UnboundedRepeat_ShouldReturnNullCount()
    {
        var timer = new TimerDefinition(TimerType.Cycle, "R/PT1H");
        var (repeatCount, interval) = timer.ParseCycle();
        Assert.IsNull(repeatCount);
        Assert.AreEqual(TimeSpan.FromHours(1), interval);
    }

    [TestMethod]
    public void GetDueTime_Duration_ISO8601_Hours()
    {
        var timer = new TimerDefinition(TimerType.Duration, "PT2H30M");
        var dueTime = timer.GetDueTime();
        Assert.AreEqual(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(30), dueTime);
    }
}
