using System.Globalization;
using System.Text.RegularExpressions;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

[TestClass]
[TestCategory("E2E")]
public class ProcessInstancesLocalizationTests : WorkflowE2ETestBase
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("en")]
    [DataRow("ru")]
    public async Task StatusesDatesAndNavigation_AreLocalized_AndVersionRoutesRemainUnchanged(string culture)
    {
        var ru = culture == "ru";
        var suffix = InlineBpmn.UniqueSuffix();
        var key = $"e2e-localized-instances-{suffix}";
        var message = $"localized-message-{suffix}";
        var xml = InlineBpmn.MessageCatch(key, message);
        var v1 = await ApiClient.DeployAsync(xml);
        var first = await ApiClient.StartAsync(key, new() { ["requestId"] = $"first-{suffix}" });
        await ApiClient.WaitForStateAsync(first.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));
        var second = await ApiClient.StartAsync(key, new() { ["requestId"] = $"second-{suffix}" });
        await ApiClient.WaitForStateAsync(second.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));
        // Completion order differs from creation/start order, so each sort must use its own property.
        await ApiClient.SendMessageAsync(message, $"second-{suffix}");
        await ApiClient.WaitForCompletionAsync(second.WorkflowInstanceId);
        await ApiClient.SendMessageAsync(message, $"first-{suffix}");
        await ApiClient.WaitForCompletionAsync(first.WorkflowInstanceId);
        var v2 = await ApiClient.DeployAsync(xml);
        var businessId = $"ORDER-{suffix}";
        var running = await ApiClient.StartAsync(key, new() { ["requestId"] = $"running-{suffix}" }, businessId: businessId);
        await ApiClient.WaitForStateAsync(running.WorkflowInstanceId, s => s.ActiveActivityIds.Contains("waitMessage"));
        var failed = await ApiClient.StartAsync(key); // Missing requestId fails message-catch registration.
        await ApiClient.WaitForStateAsync(failed.WorkflowInstanceId, s => s.IsFailed);
        var projected = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.ListInstancesAsync(key, ct: t),
            p => p.TotalCount == 4 && p.Items.Count(i => i.IsCompleted) == 3 && p.Items.Any(i => i.IsFailed),
            "two completed, one failed, and one running instance");

        await SetCultureAsync(culture);
        var instances = new ProcessInstancesPage(Page);
        await instances.OpenAsync(key);
        await Expect(Page).ToHaveTitleAsync(ru ? $"Экземпляры - {key}" : $"Instances - {key}");
        await Expect(instances.Title(ru ? $"Экземпляры — {key} (все версии)" : $"Instances — {key} (all versions)"))
            .ToBeVisibleAsync();
        await Expect(instances.Rows).ToHaveCountAsync(4);
        await instances.AssertInstanceStatusAsync(first.WorkflowInstanceId, ru ? "Завершён" : "Completed");
        await instances.AssertInstanceStatusAsync(running.WorkflowInstanceId, ru ? "Выполняется" : "Running");
        await instances.AssertInstanceStatusAsync(failed.WorkflowInstanceId, ru ? "Ошибка" : "Failed");
        await Expect(instances.BusinessIdCell(running.WorkflowInstanceId)).ToHaveTextAsync(businessId);
        await Expect(instances.BusinessIdCell(first.WorkflowInstanceId)).ToHaveTextAsync("-");
        var firstRow = projected.Items.Single(i => i.InstanceId == first.WorkflowInstanceId);
        await AssertTimestampAsync(instances.Row(firstRow.InstanceId), "instance-created-at", firstRow.CreatedAt!.Value, culture);
        await AssertTimestampAsync(instances.Row(firstRow.InstanceId), "instance-started-at", firstRow.ExecutionStartedAt!.Value, culture);
        await AssertTimestampAsync(instances.Row(firstRow.InstanceId), "instance-completed-at", firstRow.CompletedAt!.Value, culture);
        await Expect(instances.Row(running.WorkflowInstanceId).GetByTestId("instance-completed-at")).ToHaveCountAsync(0);
        await Expect(instances.StateVersionCell(firstRow.InstanceId)).ToHaveTextAsync(firstRow.StateVersion.ToString(CultureInfo.GetCultureInfo(culture)));
        await Expect(instances.StateVersionCell(firstRow.InstanceId)).ToHaveAttributeAsync("title", ru
            ? "Версия журнала, отражённая в этой строке; текущее состояние экземпляра может быть новее"
            : "Journal version this row reflects; the live instance may be ahead of it");

        foreach (var width in new[] { 768, 1280, 1920 })
        {
            await Page.SetViewportSizeAsync(width, 900);
            await Page.WaitForFunctionAsync("""
                () => {
                    const host = document.querySelector('.process-instances-grid-container');
                    const style = getComputedStyle(host);
                    const available = host.parentElement.clientWidth
                        - parseFloat(style.marginLeft) - parseFloat(style.marginRight);
                    const grid = host.querySelector('table');
                    const cells = [...grid.querySelectorAll('tbody td')];
                    return Math.abs(host.getBoundingClientRect().width - available) < 2 &&
                        grid.getBoundingClientRect().width >= host.clientWidth - 1 && cells.every(cell => {
                            const r = cell.getBoundingClientRect();
                            return [...cell.querySelectorAll('fluent-button,time,fluent-badge')].every(el => {
                                const b = el.getBoundingClientRect();
                                return b.left >= r.left - 1 && b.right <= r.right + 1 &&
                                    b.top >= r.top - 1 && b.bottom <= r.bottom + 1;
                            });
                        });
                }
                """);
            if (ru && width == 1280)
            {
                var screenshot = Path.Combine(TestContext.TestRunResultsDirectory!, "instances-ru-1280.png");
                await Page.ScreenshotAsync(new() { Path = screenshot, FullPage = true });
                TestContext.AddResultFile(screenshot);
            }
        }

        await instances.OpenAsync(key, v1.Version);
        await Expect(instances.Title(ru ? $"Экземпляры — {key} v{v1.Version}" : $"Instances — {key} v{v1.Version}"))
            .ToBeVisibleAsync();
        await Expect(instances.Rows).ToHaveCountAsync(2);
        await Expect(instances.Row(running.WorkflowInstanceId)).ToHaveCountAsync(0);
        foreach (var (heading, ascendingFirst, descendingFirst) in new[]
        {
            (ru ? "Создан (UTC)" : "Created At (UTC)", first.WorkflowInstanceId, second.WorkflowInstanceId),
            (ru ? "Запущен (UTC)" : "Started At (UTC)", first.WorkflowInstanceId, second.WorkflowInstanceId),
            (ru ? "Завершён (UTC)" : "Completed At (UTC)", second.WorkflowInstanceId, first.WorkflowInstanceId)
        })
        {
            var sort = instances.Grid.Locator("fluent-button.col-sort-button").Filter(new() { HasTextString = heading });
            await sort.ClickAsync();
            await Expect(instances.Rows.First.Locator("span[title]").First).ToHaveAttributeAsync("title", ascendingFirst.ToString());
            await sort.ClickAsync();
            await Expect(instances.Rows.First.Locator("span[title]").First).ToHaveAttributeAsync("title", descendingFirst.ToString());
        }

        await instances.OpenAsync(key, v2.Version);
        await Expect(instances.Rows).ToHaveCountAsync(2);
        await instances.Row(running.WorkflowInstanceId).GetByRole(AriaRole.Button, new() { Name = ru ? "Открыть" : "View", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/process-instance/{running.WorkflowInstanceId:D}$"));
        await instances.OpenAsync(key);
        await Page.GetByRole(AriaRole.Button, new() { Name = ru ? "Назад" : "Go back", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/workflows$"));
        await ApiClient.SendMessageAsync(message, $"running-{suffix}");
        await ApiClient.WaitForCompletionAsync(running.WorkflowInstanceId);
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("ru")]
    public async Task PaginationAndEmptyState_AreLocalized_WithoutLosingInstances(string culture)
    {
        var ru = culture == "ru";
        var key = $"e2e-localized-instance-pages-{InlineBpmn.UniqueSuffix()}";
        await ApiClient.DeployAsync(InlineBpmn.ScriptOnly(key));
        var ids = new HashSet<Guid>();
        for (var i = 0; i < 21; i++)
            ids.Add((await ApiClient.StartAsync(key)).WorkflowInstanceId);
        await ApiClient.WaitForInstancesPageAsync(t => ApiClient.ListInstancesAsync(key, ct: t),
            p => p.TotalCount == 21 && p.Items.All(i => i.IsCompleted), "all 21 completed instances");
        await SetCultureAsync(culture);
        var instances = new ProcessInstancesPage(Page);
        await instances.OpenAsync(key);
        await Expect(instances.Rows).ToHaveCountAsync(20);
        var firstPage = await VisibleIdsAsync(instances);
        var position = Page.GetByTestId("instances-page-number");
        await Expect(position).ToHaveTextAsync(ru ? "Страница 1 из 2" : "Page 1 of 2");
        await Expect(Page.GetByTestId("instances-next-page")).ToHaveTextAsync(ru ? "Следующая" : "Next");
        await Page.GetByTestId("instances-next-page").ClickAsync();
        await Expect(instances.Rows).ToHaveCountAsync(1);
        await Expect(position).ToHaveTextAsync(ru ? "Страница 2 из 2" : "Page 2 of 2");
        var lastPage = await VisibleIdsAsync(instances);
        Assert.IsFalse(firstPage.Overlaps(lastPage));
        firstPage.UnionWith(lastPage);
        Assert.IsTrue(ids.SetEquals(firstPage), "Pagination must retain every instance exactly once.");
        await Page.GetByTestId("instances-previous-page").ClickAsync();
        await Expect(instances.Rows).ToHaveCountAsync(20);
        await Page.GetByTestId("instances-last-page").ClickAsync();
        await Expect(instances.Rows).ToHaveCountAsync(1);
        await Page.GetByTestId("instances-first-page").ClickAsync();
        await Expect(instances.Rows).ToHaveCountAsync(20);

        await instances.OpenAsync($"{key}-missing");
        await Expect(Page.GetByTestId("process-instances-empty")).ToHaveTextAsync(ru ? "Экземпляры не найдены." : "No instances found.");
        await Expect(Page.GetByTestId("process-instances-pagination")).ToHaveTextAsync(ru ? "Всего: 0" : "Total: 0");
        await Expect(Page.GetByTestId("instances-next-page")).ToHaveCountAsync(0);
    }

    private static async Task<HashSet<Guid>> VisibleIdsAsync(ProcessInstancesPage instances) =>
        (await instances.Grid.Locator("tbody td:first-child span[title]").EvaluateAllAsync<string[]>(
            "elements => elements.map(el => el.title)")).Select(Guid.Parse).ToHashSet();

    private static async Task AssertTimestampAsync(ILocator row, string testId, DateTimeOffset timestamp, string culture)
    {
        var time = row.GetByTestId(testId);
        await Expect(time).ToHaveTextAsync(timestamp.UtcDateTime.ToString("G", CultureInfo.GetCultureInfo(culture)));
        await Expect(time).ToHaveAttributeAsync("datetime", timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private Task SetCultureAsync(string culture) => Context.AddCookiesAsync([new Cookie
    {
        Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = AspireFixture.WebBaseUri.ToString()
    }]);
}
