using System.Globalization;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

[TestClass]
[TestCategory("E2E")]
public class WorkflowsLocalizationTests : WorkflowE2ETestBase
{
    [TestMethod]
    [DataRow("en", 1280)]
    [DataRow("ru", 1280)]
    [DataRow("en", 768)]
    [DataRow("ru", 768)]
    [DataRow("en", 1920)]
    [DataRow("ru", 1920)]
    public async Task ActionButtons_StayOnOneLine_WithoutOverlapping(string culture, int width)
    {
        var key = $"e2e-workflows-layout-{InlineBpmn.UniqueSuffix()}";
        var xml = InlineBpmn.ScriptOnly(key);
        await ApiClient.DeployAsync(xml);
        await ApiClient.DeployAsync(xml);
        await SetCultureAsync(culture);
        await Page.SetViewportSizeAsync(width, 900);
        var workflows = new WorkflowsPage(Page);
        await workflows.OpenAsync();
        await workflows.SearchAsync(key);
        await workflows.ToggleVersionsAsync(key);
        await Expect(workflows.AllRows(key)).ToHaveCountAsync(2);

        // Fluent's grid rows use display: contents, so measure the cells themselves.
        await Page.WaitForFunctionAsync("""
            () => {
                const container = document.querySelector('.workflows-grid-container');
                const grid = container.querySelector('.workflows-grid');
                const style = getComputedStyle(container);
                const parentStyle = getComputedStyle(container.parentElement);
                const availableWidth = container.parentElement.clientWidth
                    - parseFloat(parentStyle.paddingLeft) - parseFloat(parentStyle.paddingRight)
                    - parseFloat(style.marginLeft) - parseFloat(style.marginRight);
                if (Math.abs(container.getBoundingClientRect().width - availableWidth) > 1 ||
                    grid.getBoundingClientRect().width < container.clientWidth - 1) return false;
                const groups = [...document.querySelectorAll('.workflows-actions')];
                return groups.length === 2 && groups.every(group => {
                    const cell = group.closest('td').getBoundingClientRect();
                    const buttons = [...group.querySelectorAll('fluent-button')].map(b => b.getBoundingClientRect());
                    return buttons.length >= 4 && buttons.every((b, i) =>
                        b.width > 0 && b.height > 0 &&
                        Math.abs(b.top - buttons[0].top) < 1 &&
                        b.top >= cell.top - 1 && b.bottom <= cell.bottom + 1 &&
                        b.left >= cell.left - 1 && b.right <= cell.right + 1 &&
                        buttons.slice(i + 1).every(other =>
                            b.right <= other.left || other.right <= b.left ||
                            b.bottom <= other.top || other.bottom <= b.top));
                });
            }
            """);
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("ru")]
    public async Task ActionsAndDates_UseSelectedCulture_AndPreserveIdentifiers(string culture)
    {
        var ru = culture == "ru";
        var key = $"e2e-localized-workflows-{InlineBpmn.UniqueSuffix()}";
        var businessId = $"ORDER-{InlineBpmn.UniqueSuffix()}";
        var deployed = await ApiClient.DeployAsync(InlineBpmn.ScriptOnly(key));
        await SetCultureAsync(culture);
        var workflows = new WorkflowsPage(Page);
        await workflows.OpenAsync();
        await workflows.SearchAsync(key);

        await Expect(Page).ToHaveTitleAsync(ru ? "Процессы" : "Workflows");
        await Expect(Page.GetByRole(AriaRole.Heading, new()
        {
            Name = ru ? "Зарегистрированные процессы" : "Registered Workflows", Exact = true
        })).ToBeVisibleAsync();
        await Expect(workflows.SearchInput).ToHaveAttributeAsync("placeholder",
            ru ? "Поиск по ключу процесса…" : "Search by process key");
        var time = workflows.Row(key).GetByTestId("workflow-deployed-at");
        var timestamp = DateTimeOffset.Parse((await time.GetAttributeAsync("datetime"))!, CultureInfo.InvariantCulture);
        await Expect(time).ToHaveTextAsync(timestamp.UtcDateTime.ToString("g", CultureInfo.GetCultureInfo(culture)));

        await workflows.ClickActionAsync(key, ru ? "Отключить" : "Disable");
        await Expect(workflows.SuccessMessage).ToHaveTextAsync(ru ? $"Процесс '{key}' отключён." : $"Disabled '{key}'.");
        await Expect(workflows.ActionButton(key, ru ? "Запустить" : "Start")).ToHaveAttributeAsync("disabled", "");
        Assert.IsFalse((await ApiClient.ListDefinitionsAsync($"ProcessDefinitionKey=={key}")).Items.Single().IsActive);
        await workflows.ClickActionAsync(key, ru ? "Включить" : "Enable");
        await Expect(workflows.SuccessMessage).ToHaveTextAsync(ru ? $"Процесс '{key}' включён." : $"Enabled '{key}'.");

        await workflows.StartBusinessIdInput.FillAsync($" {businessId}");
        await workflows.StartBusinessIdInput.PressAsync("Tab");
        await workflows.ClickActionAsync(key, ru ? "Запустить" : "Start");
        await Expect(Page.GetByTestId("workflows-action-error")).ToContainTextAsync(
            ru ? "Не удалось запустить процесс: " : "Failed to start workflow: ");
        await Expect(workflows.StartBusinessIdInput).ToHaveValueAsync($" {businessId}");

        await workflows.StartBusinessIdInput.FillAsync(businessId);
        await workflows.StartBusinessIdInput.PressAsync("Tab");
        await workflows.ClickActionAsync(key, ru ? "Запустить" : "Start");
        await Expect(workflows.SuccessMessage).ToContainTextAsync(ru
            ? $"Запущен процесс '{key}' v{deployed.Version}. Экземпляр: "
            : $"Started '{key}' v{deployed.Version}. Instance: ");
        await Expect(workflows.SuccessMessage).ToContainTextAsync(businessId);
        await Expect(workflows.StartBusinessIdInput).ToHaveValueAsync("");
        var instances = await ApiClient.WaitForInstancesPageAsync(
            t => ApiClient.ListInstancesByBusinessIdAsync(key, businessId, t),
            p => p.TotalCount == 1, "the instance with the unchanged business id");
        await ApiClient.WaitForCompletionAsync(instances.Items.Single().InstanceId);
    }

    [TestMethod]
    [DataRow("en")]
    [DataRow("ru")]
    public async Task PaginationAndEmptyState_UseSelectedCulture_AndSearchResetsPage(string culture)
    {
        var ru = culture == "ru";
        var prefix = $"e2e-localized-pages-{InlineBpmn.UniqueSuffix()}";
        // Reverse deployment order so date sorting cannot accidentally pass with key sorting.
        for (var i = 20; i >= 0; i--)
            await ApiClient.DeployAsync(InlineBpmn.ScriptOnly($"{prefix}-{i:D2}"));
        await SetCultureAsync(culture);
        var workflows = new WorkflowsPage(Page);
        await workflows.OpenAsync();
        await workflows.SearchInput.FillAsync(prefix);
        await workflows.SearchInput.PressAsync("Enter");
        var rows = workflows.Grid.Locator("tbody tr");
        await Expect(rows).ToHaveCountAsync(20);
        await Expect(rows.First).ToContainTextAsync($"{prefix}-00");
        var pageNumber = Page.GetByTestId("workflows-page-number");
        await Expect(pageNumber).ToHaveTextAsync(ru ? "Страница 1 из 2" : "Page 1 of 2");
        await Expect(Page.GetByTestId("workflows-next-page")).ToHaveTextAsync(ru ? "Следующая" : "Next");
        await Page.GetByTestId("workflows-next-page").ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows.First).ToContainTextAsync($"{prefix}-20");
        await Expect(pageNumber).ToHaveTextAsync(ru ? "Страница 2 из 2" : "Page 2 of 2");
        await Page.GetByTestId("workflows-previous-page").ClickAsync();
        await Expect(rows).ToHaveCountAsync(20);
        await Page.GetByTestId("workflows-last-page").ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        await Page.GetByTestId("workflows-first-page").ClickAsync();
        await Expect(rows).ToHaveCountAsync(20);
        await Page.GetByTestId("workflows-last-page").ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        await workflows.SearchAsync($"{prefix}-00");
        await Expect(pageNumber).ToHaveTextAsync(ru ? "Страница 1 из 1" : "Page 1 of 1");
        await workflows.SearchInput.FillAsync($"{prefix}-missing");
        await workflows.SearchInput.PressAsync("Enter");
        await Expect(Page.GetByTestId("workflows-empty")).ToHaveTextAsync(ru ? "Процессы не найдены." : "No workflows found.");
        await workflows.SearchInput.FillAsync(prefix);
        await workflows.SearchInput.PressAsync("Enter");
        await Expect(rows).ToHaveCountAsync(20);
        var dateSort = workflows.Grid.Locator("fluent-button.col-sort-button");
        await Expect(dateSort).ToContainTextAsync(ru ? "Дата публикации (UTC)" : "Deployed at (UTC)");
        await dateSort.ClickAsync();
        await Expect(rows.First).ToContainTextAsync($"{prefix}-20");
        await dateSort.ClickAsync();
        await Expect(rows.First).ToContainTextAsync($"{prefix}-00");
    }

    private Task SetCultureAsync(string culture) => Context.AddCookiesAsync([new Cookie
    {
        Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = AspireFixture.WebBaseUri.ToString()
    }]);
}
