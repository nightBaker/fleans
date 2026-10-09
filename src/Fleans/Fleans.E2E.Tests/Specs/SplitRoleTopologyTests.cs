using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Fleans.E2E.Tests.ApiClient;
using Fleans.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/55-plugin-host-isolation/test-plan.md (Scenarios 3, 4-equivalent, 5),
// tests/manual/58-custom-task-cancellation/test-plan.md (boundary-timer interrupt variant) and
// tests/manual/41-placement-role-mismatch/test-plan.md (role mismatch fails loudly).
//
// Needs the split Core / Worker / Plugin topology: run with FLEANS_SPLIT_ROLES=true, which
// makes the AppHost run fleans-core as Core plus a dedicated fleans-worker silo, and makes
// AspireFixture attach the test-only Plugin-role host (src/Fleans/Fleans.E2E.PluginHost,
// which hosts the `e2e-probe` plugin and nothing else). CI: dotnet.yml `e2e-split-roles` job.
[TestClass]
[TestCategory("E2E")]
[TestCategory(E2ECategories.SplitRoles)]
public class SplitRoleTopologyTests : WorkflowE2ETestBase
{
    private const string ProbeTaskType = "e2e-probe";

    private static readonly HashSet<string> ExitedStates =
        [KnownResourceStates.Exited, KnownResourceStates.Finished, KnownResourceStates.FailedToStart];

    [TestInitialize]
    public void RequireSplitTopology()
    {
        if (!AspireFixture.IsSplitRoles)
        {
            Assert.Inconclusive(
                $"Needs the split-role topology — run with {AspireFixture.SplitRolesEnvVar}=true " +
                $"(filter TestCategory={E2ECategories.SplitRoles}).");
        }
    }

    // Plan 55 Scenario 3: a plugin compiled only into the external Plugin-role host is
    // catalogued against that host alone and executes there — never on the core-/worker- engine
    // silos, which don't have the handler assembly loaded.
    [TestMethod]
    public async Task CustomTask_ExecutesOnPluginHost_NotOnEngineSilos()
    {
        var entry = await WaitForCatalogEntryAsync(ProbeTaskType);
        Assert.IsTrue(entry.SiloNames.Count > 0, "e2e-probe catalog entry lists no silos.");
        foreach (var catalogued in entry.SiloNames)
        {
            Assert.IsTrue(catalogued.StartsWith("plugin-", StringComparison.Ordinal),
                $"e2e-probe must be catalogued only against plugin- silos; got [{string.Join(", ", entry.SiloNames)}].");
        }

        var xml = BpmnFixtureLoader.Load("55-plugin-host-isolation", "probe-placement.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(deployed.ProcessDefinitionKey);

        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));

        state.AssertCompletedActivities("start", "probe", "end");
        var silo = state.GetVariable("probeSilo");
        Assert.IsTrue(silo.StartsWith("plugin-", StringComparison.Ordinal),
            $"e2e-probe executed on '{silo}', expected the plugin- host.");
        CollectionAssert.Contains(entry.SiloNames.ToList(), silo,
            "The silo the probe ran on must be the one the catalog advertises.");
        state.AssertVariableEquals("probeRole", "Plugin");
    }

    // Plan 58 (boundary-timer variant): an interrupting boundary timer on a long-running plugin
    // task hosted on another silo takes the timeout path, and the plugin's eventual completion
    // is dropped by the stale-activity guard instead of resurrecting the normal path.
    [TestMethod]
    public async Task BoundaryTimer_InterruptsLongRunningPluginTask_TimeoutPathTaken_LateCompletionDropped()
    {
        await WaitForCatalogEntryAsync(ProbeTaskType);
        var probeKey = Guid.NewGuid().ToString("N");

        var xml = BpmnFixtureLoader.Load("58-custom-task-cancellation", "probe-boundary-timer.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        var started = await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?>
            {
                ["delayMs"] = 10_000, // well past the PT3S boundary timer
                ["callbackUrl"] = AspireFixture.TestHttpServerBaseUrl,
                ["probeKey"] = probeKey,
            });

        await WaitForProbeEventAsync(probeKey, "started", TimeSpan.FromSeconds(20));

        // 75s, not 30s: an interrupting boundary timer currently stalls ~30s on a
        // WorkflowInstance <-> TimerCallbackGrain call cycle (HandleTimerFired -> Cancel on the
        // grain that is awaiting HandleTimerFired) until the Orleans response timeout breaks it.
        // See #785.
        var state = await ApiClient.WaitForCompletionAsync(
            started.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(75));
        state.AssertCompletedActivities("timeoutPath", "endTimeout");
        state.AssertNotCompleted("normalPath", "endNormal");
        state.AssertVariableEquals("timedOut", "True");

        // Let the plugin finish (or observe a cancellation, once #786 lands) and prove the
        // late CompleteActivity didn't change the finished instance.
        var terminal = await WaitForProbeEventAsync(
            probeKey, e => e.Event is "completed" or "cancelled", TimeSpan.FromSeconds(30));
        Assert.IsTrue(terminal.Silo.StartsWith("plugin-", StringComparison.Ordinal),
            $"Long-running probe ran on '{terminal.Silo}', expected the plugin- host.");
        await Task.Delay(TimeSpan.FromSeconds(2));

        var after = await ApiClient.GetStateAsync(started.WorkflowInstanceId)
            ?? throw new AssertFailedException("Instance state disappeared.");
        Assert.IsTrue(after.IsCompleted, "Instance must stay completed.");
        after.AssertNotCompleted("normalPath", "endNormal");
        Assert.IsFalse(after.TryGetVariable("probeSilo", out _),
            "The interrupted task's late output mapping must not be applied.");
    }

    // Plan 58 target behaviour: interrupting the activity cancels the token handed to the
    // plugin. Today only grain deactivation cancels it (#568), so the probe runs to completion.
    [TestMethod]
    [Ignore("Engine gap #786: interrupting a custom-task activity does not signal the handler's CancellationToken.")]
    public async Task BoundaryTimer_InterruptingPluginTask_CancelsHandlerToken()
    {
        await WaitForCatalogEntryAsync(ProbeTaskType);
        var probeKey = Guid.NewGuid().ToString("N");

        var xml = BpmnFixtureLoader.Load("58-custom-task-cancellation", "probe-boundary-timer.bpmn");
        var deployed = await ApiClient.DeployAsync(xml);
        await ApiClient.StartAsync(
            deployed.ProcessDefinitionKey,
            variables: new Dictionary<string, object?>
            {
                ["delayMs"] = 60_000,
                ["callbackUrl"] = AspireFixture.TestHttpServerBaseUrl,
                ["probeKey"] = probeKey,
            });

        await WaitForProbeEventAsync(probeKey, "started", TimeSpan.FromSeconds(20));
        // PT3S timer + generous propagation budget, far below the 60s delay.
        var terminal = await WaitForProbeEventAsync(
            probeKey, e => e.Event is "completed" or "cancelled", TimeSpan.FromSeconds(20));
        Assert.AreEqual("cancelled", terminal.Event,
            "The plugin's CancellationToken must fire when the boundary timer interrupts the task.");
    }

    // Role mismatch fails loudly. Plan 41's startup PlacementRoleAssertion is currently dead
    // code (#787), so this exercises the explicit Fleans:Role validation that does run (plan 55
    // Scenarios 4/5, against the real binaries): an engine worker started as Plugin, and a
    // plugin host started as Worker, each refuse to boot with an explicit error instead of
    // joining the cluster under the wrong role.
    [TestMethod]
    [DataRow(AspireFixture.MisroledWorkerResource,
        "Fleans.WorkerHost does not support Fleans:Role=Plugin")]
    [DataRow(AspireFixture.MisroledPluginHostResource,
        "Fleans:Role='Worker' is not valid for a custom plugin host")]
    public async Task MisroledSilo_RefusesToStart_WithExplicitRoleError(string resource, string expectedError)
    {
        var app = AspireFixture.Application;
        var commands = app.Services.GetRequiredService<ResourceCommandService>();

        var result = await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand);
        Assert.IsTrue(result.Success, $"Start command for {resource} failed: {result.ErrorMessage}");

        // Primary signal: the process dies with the explicit role error (unhandled exception).
        var logs = await ReadLogsUntilAsync(resource, expectedError, TimeSpan.FromMinutes(2));
        StringAssert.Contains(logs, expectedError,
            $"{resource} did not report the explicit role error. Captured logs:\n{logs}");
        StringAssert.Contains(logs, "Unhandled exception",
            $"{resource} must crash on the role error, not log-and-continue. Captured logs:\n{logs}");

        // Secondary: when Aspire reports the exit (path-based projects launch through a
        // `dotnet run` wrapper whose exit DCP doesn't always surface promptly), it is non-zero.
        using var exitWait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var exited = await app.ResourceNotifications.WaitForResourceAsync(
                resource,
                e => e.Snapshot.ExitCode is not null || ExitedStates.Contains(e.Snapshot.State?.Text ?? ""),
                exitWait.Token);
            Assert.AreNotEqual(0, exited.Snapshot.ExitCode,
                $"{resource} must exit non-zero; state={exited.Snapshot.State?.Text}, exitCode={exited.Snapshot.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            await commands.ExecuteCommandAsync(resource, KnownResourceCommands.StopCommand);
        }

        // The rest of the split cluster is unaffected.
        var probe = await ApiClient.StartAsync(
            (await ApiClient.DeployAsync(BpmnFixtureLoader.Load("55-plugin-host-isolation", "probe-placement.bpmn")))
                .ProcessDefinitionKey);
        var state = await ApiClient.WaitForCompletionAsync(probe.WorkflowInstanceId, timeout: TimeSpan.FromSeconds(30));
        state.AssertCompletedActivities("probe", "end");
    }

    private sealed record CatalogEntry(string TaskType, string? DisplayName, List<string> SiloNames);

    private static async Task<CatalogEntry> WaitForCatalogEntryAsync(string taskType)
    {
        // The plugin host registers its plugins with the catalog grain asynchronously after
        // joining the cluster.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            using var response = await AspireFixture.ApiHttpClient.GetAsync($"/custom-tasks/{taskType}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var entry = await response.Content.ReadFromJsonAsync<CatalogEntry>();
                if (entry is { SiloNames.Count: > 0 })
                {
                    return entry;
                }
            }
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Custom task '{taskType}' never appeared in /custom-tasks (last status {(int)response.StatusCode}).");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    private static Task<TestHttpServer.ProbeEvent> WaitForProbeEventAsync(string key, string evt, TimeSpan timeout) =>
        WaitForProbeEventAsync(key, e => e.Event == evt, timeout);

    private static async Task<TestHttpServer.ProbeEvent> WaitForProbeEventAsync(
        string key, Func<TestHttpServer.ProbeEvent, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = TestHttpServer.ProbeEvents(key).FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        throw new TimeoutException(
            $"No matching e2e-probe report for key {key} within {timeout}. Seen: " +
            $"[{string.Join(", ", TestHttpServer.ProbeEvents(key).Select(e => e.Event))}].");
    }

    private static async Task<string> ReadLogsUntilAsync(string resource, string needle, TimeSpan timeout)
    {
        // AspireFixture tees every resource's console output to resource-logs/; the log
        // stream can trail the Exited state notification slightly, so poll.
        var deadline = DateTimeOffset.UtcNow + timeout;
        var logs = AspireFixture.ReadCapturedLogs(resource);
        while (!logs.Contains(needle, StringComparison.Ordinal) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            logs = AspireFixture.ReadCapturedLogs(resource);
        }
        return logs;
    }
}
