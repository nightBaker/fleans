using System.Globalization;
using System.Text.RegularExpressions;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/08-timer-events/test-plan.md (Scenarios C + D — Timer Start Event)
[TestClass]
[TestCategory("E2E")]
public class TimerStartEventTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task TimerStartEvent_TimeDate_CreatesOneInstanceAtTheConfiguredMoment()
    {
        const string key = "timer-start-date-test";
        var fireAt = DateTimeOffset.UtcNow.AddSeconds(3);
        var xml = Regex.Replace(
            BpmnFixtureLoader.Load("08-timer-events", "timer-start-date.bpmn"),
            "<timeDate>[^<]*</timeDate>",
            $"<timeDate>{fireAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}</timeDate>");

        var before = await ApiClient.GetInstancesForKeyAsync(key);
        var ignore = before.Select(i => i.InstanceId).ToHashSet();

        try
        {
            await ApiClient.DeployAsync(xml);
            // A redeploy inherits the previous version's disabled state (the finally block
            // disables the key so leftover timers stop); re-enable so the scheduler arms.
            using (await ApiClient.EnableAsync(key)) { }

            var created = await ApiClient.WaitForInstancesForKeyAsync(
                key, ignore, list => list.Count >= 1, timeout: TimeSpan.FromSeconds(30));

            var state = await ApiClient.WaitForCompletionAsync(created[0].InstanceId);
            state.AssertCompletedActivities("timerStart", "afterTimerStart", "end");
            state.AssertVariableEquals("startedByTimer", "True");
            Assert.IsNotNull(state.CreatedAt);
            Assert.IsGreaterThanOrEqualTo(fireAt.AddSeconds(-1), state.CreatedAt.Value,
                "The instance must not be created before the configured timeDate.");

            var after = await ApiClient.GetInstancesForKeyAsync(key);
            Assert.HasCount(1, after.Where(i => !ignore.Contains(i.InstanceId)).ToList(),
                "timeDate start event must create exactly one instance.");
        }
        finally
        {
            using var _ = await ApiClient.DisableAsync(key);
        }
    }

    [TestMethod]
    public async Task TimerStartEvent_TimeCycle_CreatesOneInstancePerRepetition()
    {
        const string key = "timer-start-cycle-test";
        var xml = BpmnFixtureLoader.Load("08-timer-events", "timer-start-cycle.bpmn");

        var before = await ApiClient.GetInstancesForKeyAsync(key);
        var ignore = before.Select(i => i.InstanceId).ToHashSet();

        try
        {
            await ApiClient.DeployAsync(xml);
            // A redeploy inherits the previous version's disabled state (the finally block
            // disables the key so leftover timers stop); re-enable so the scheduler arms.
            using (await ApiClient.EnableAsync(key)) { }

            // R2/PT5S → two instances, ~5s apart.
            var created = await ApiClient.WaitForInstancesForKeyAsync(
                key, ignore, list => list.Count >= 2 && list.All(i => i.IsCompleted),
                timeout: TimeSpan.FromSeconds(60));

            Assert.HasCount(2, created, "R2 cycle must create exactly two instances.");
            foreach (var instance in created)
            {
                var state = await ApiClient.GetStateAsync(instance.InstanceId);
                Assert.IsNotNull(state);
                state.AssertCompletedActivities("timerStart", "afterTimerStart", "end");
                state.AssertVariableEquals("startedByTimer", "True");
            }
        }
        finally
        {
            using var _ = await ApiClient.DisableAsync(key);
        }
    }
}
