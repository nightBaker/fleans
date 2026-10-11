using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// Ports tests/manual/31-events-page/test-plan.md (steps 1, 3, 4, 5, 6, 8) — issue #772.
// Steps 2 (empty state against a clean DB) and 7 (OIDC auth) remain manual: the E2E stack's
// SQLite file is shared with concurrently-running suites, and there is no IdP stub.
[TestClass]
[TestCategory("E2E")]
public class EventsPageTests : WorkflowE2ETestBase
{
    [TestMethod]
    public async Task SidebarEntry_NavigatesToEventsPage()
    {
        await BlazorPage.GotoInteractiveAsync(Page, "/workflows");

        await Page.Locator(".fluent-appbar-item a[href='/events']").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/events$"));
        await Expect(new EventsPage(Page).Header).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task StartEventRegistrations_AreListed_AndConditionalRowDisappearsOnDisable()
    {
        var suffix = InlineBpmn.UniqueSuffix();
        var msgKey = $"e2e-events-msgstart-{suffix}";
        var msgName = $"e2e-msgstart-{suffix}";
        var sigKey = $"e2e-events-sigstart-{suffix}";
        var sigName = $"e2e-sigstart-{suffix}";
        var condKey = $"e2e-events-condstart-{suffix}";
        var condition = $"e2eTemp{suffix} > 100";

        await ApiClient.DeployAsync(InlineBpmn.MessageStart(msgKey, msgName));
        await ApiClient.DeployAsync(InlineBpmn.SignalStart(sigKey, sigName));
        await ApiClient.DeployAsync(InlineBpmn.ConditionalStart(condKey, condition));

        var events = new EventsPage(Page);
        await events.OpenAsync();
        var urlBefore = Page.Url;

        // Message Start Events: MessageName + ProcessDefinitionKey.
        await events.RefreshUntilCountAsync(events.MessageStartRow(msgName), 1);
        await Expect(events.MessageStartRow(msgName).Locator("td")).ToHaveTextAsync([msgName, msgKey]);

        // Signal Start Events: SignalName + ProcessDefinitionKey.
        await events.RefreshUntilCountAsync(events.SignalStartRow(sigName), 1);
        await Expect(events.SignalStartRow(sigName).Locator("td")).ToHaveTextAsync([sigName, sigKey]);

        // Conditional Start Events: ProcessDefinitionKey + ActivityId + ConditionExpression.
        await events.RefreshUntilCountAsync(events.ConditionalStartRow(condKey), 1);
        await Expect(events.ConditionalStartRow(condKey).Locator("td")).ToHaveTextAsync([condKey, "condStart", condition]);

        // Refresh re-queries in place — no navigation.
        Assert.AreEqual(urlBefore, Page.Url, "Refresh must not navigate.");

        // Disabling the definition un-registers the conditional listener; the page filters it out.
        using (var disable = await ApiClient.DisableAsync(condKey))
        {
            Assert.IsTrue(disable.IsSuccessStatusCode, $"Disable failed: {disable.StatusCode}");
        }
        await events.RefreshUntilCountAsync(events.ConditionalStartRow(condKey), 0);

        // The other registrations are untouched.
        await Expect(events.MessageStartRow(msgName)).ToHaveCountAsync(1);
        await Expect(events.SignalStartRow(sigName)).ToHaveCountAsync(1);
    }

    [TestMethod]
    public async Task Plan31_EventsPage_FilterAndDisplay()
    {
        // Manual message / signal correlation as observed from the UI: an active subscription
        // is listed with its correlation data, and disappears once the engine correlates the
        // delivered message / broadcast signal (delete-on-completion semantic).
        var suffix = InlineBpmn.UniqueSuffix();
        var msgKey = $"e2e-events-msgcatch-{suffix}";
        var msgName = $"e2e-msgcatch-{suffix}";
        var correlationKey = $"corr-{suffix}";
        var sigKey = $"e2e-events-sigcatch-{suffix}";
        var sigName = $"e2e-sigcatch-{suffix}";

        await ApiClient.DeployAsync(InlineBpmn.MessageCatch(msgKey, msgName));
        await ApiClient.DeployAsync(InlineBpmn.SignalCatch(sigKey, sigName));
        var msgInstance = await ApiClient.StartAsync(msgKey, new() { ["requestId"] = correlationKey });
        var sigInstance = await ApiClient.StartAsync(sigKey);
        var msgState = await ApiClient.WaitForStateAsync(msgInstance.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));
        var sigState = await ApiClient.WaitForStateAsync(sigInstance.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitSignal"));
        var msgActivityInstanceId = msgState.ActiveActivities.Single(a => a.ActivityId == "waitMessage").ActivityInstanceId;
        var sigActivityInstanceId = sigState.ActiveActivities.Single(a => a.ActivityId == "waitSignal").ActivityInstanceId;

        var events = new EventsPage(Page);
        await events.OpenAsync();

        // Active Message Subscription row: name, correlation key, truncated ids, activity id.
        await events.RefreshUntilCountAsync(events.MessageSubscriptionRow(msgName), 1);
        var msgRow = events.MessageSubscriptionRow(msgName);
        // Tooltip cells also contain the full GUID (FluentTooltip content), hence ToContainText.
        await Expect(msgRow.Locator("td")).ToContainTextAsync([
            msgName,
            correlationKey,
            Truncate(msgInstance.WorkflowInstanceId),
            "waitMessage",
            Truncate(msgActivityInstanceId),
        ]);
        await Expect(events.TruncatedId(msgRow, msgInstance.WorkflowInstanceId)).ToBeVisibleAsync();

        // Active Signal Subscription row.
        await events.RefreshUntilCountAsync(events.SignalSubscriptionRow(sigName), 1);
        var sigRow = events.SignalSubscriptionRow(sigName);
        await Expect(sigRow.Locator("td")).ToContainTextAsync([
            sigName,
            Truncate(sigInstance.WorkflowInstanceId),
            "waitSignal",
            Truncate(sigActivityInstanceId),
        ]);

        // Deliver the message (correlated by key) and broadcast the signal.
        var delivery = await ApiClient.SendMessageAsync(msgName, correlationKey);
        Assert.IsTrue(delivery.Delivered, "Message should correlate to the waiting instance.");
        var broadcast = await ApiClient.SendSignalAsync(sigName);
        Assert.AreEqual(1, broadcast.DeliveredCount, "Signal should reach exactly the one subscriber.");
        await ApiClient.WaitForCompletionAsync(msgInstance.WorkflowInstanceId);
        await ApiClient.WaitForCompletionAsync(sigInstance.WorkflowInstanceId);

        // Rows vanish after Refresh — the engine removed the subscriptions.
        await events.RefreshUntilCountAsync(events.MessageSubscriptionRow(msgName), 0);
        await events.RefreshUntilCountAsync(events.SignalSubscriptionRow(sigName), 0);
    }

    private static string Truncate(Guid id) => id.ToString("D")[..8] + "…";
}
