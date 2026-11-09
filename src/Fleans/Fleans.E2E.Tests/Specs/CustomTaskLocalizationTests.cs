using System.Globalization;
using Fleans.E2E.Tests.Infrastructure;
using Fleans.E2E.Tests.PageObjects;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

[TestClass]
[TestCategory("E2E")]
public class CustomTaskCatalogLocalizationTests : WorkflowE2ETestBase
{
    [TestMethod]
    [DataRow("en")]
    [DataRow("ru")]
    public async Task GrpcWorkerCatalog_AndUnregisteredTask_UseSelectedLanguage(string culture)
    {
        // The shared fixture starts a gRPC worker in every topology. Wait for it instead of
        // asserting an empty catalog while the worker races to register.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var entries = await ApiClient.GetCustomTasksAsync();
        while (entries.All(e => e.TaskType != "e2e-ext-probe") && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entries = await ApiClient.GetCustomTasksAsync();
        }
        var worker = entries.Single(e => e.TaskType == "e2e-ext-probe");
        await Context.AddCookiesAsync([new Cookie
        {
            Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = AspireFixture.WebBaseUri.ToString()
        }]);
        var ru = culture == "ru";
        await BlazorPage.GotoInteractiveAsync(Page, "/admin/custom-tasks");
        await Expect(Page).ToHaveTitleAsync(ru ? "Обработчики задач — Fleans" : "Custom Tasks — Fleans");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = ru ? "Обработчики задач" : "Custom Tasks", Exact = true })).ToBeVisibleAsync();
        var catalog = new CustomTaskCatalogPage(Page);
        await Expect(catalog.Cells(worker.TaskType)).ToHaveTextAsync([
            worker.TaskType, worker.DisplayName ?? "—", "—", ru ? "Публичный" : "Public", "—", string.Join(", ", worker.SiloNames), "—",
            (worker.ParameterSchema?.Parameters.Count ?? 0).ToString("N0", CultureInfo.GetCultureInfo(culture))
        ]);
        await Expect(catalog.Grid.GetByRole(AriaRole.Columnheader, new()
        {
            Name = ru ? "Узлы / воркеры" : "Silos / workers", Exact = true
        })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("custom-task-catalog-empty")).ToHaveCountAsync(0);

        await CustomTaskLocalization.OpenServiceTaskAsync(Page, "unregistered-task");
        await Expect(Page.GetByText(ru ? "Плагин (обработчик задачи)" : "Plugin (custom task)", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByText(ru
            ? "Тип задачи «unregistered-task» не зарегистрирован в каталоге."
            : "Task type \"unregistered-task\" is not registered in the catalog.", new() { Exact = false })).ToBeVisibleAsync();
    }
}

[TestClass]
[TestCategory("E2E")]
[TestCategory(E2ECategories.SplitRoles)]
public class CustomTaskPluginLocalizationTests : WorkflowE2ETestBase
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("en")]
    [DataRow("ru")]
    public async Task RegisteredCatalog_ResizeControls_AndParameterEditor_AreLocalized(string culture)
    {
        await Context.AddCookiesAsync([new Cookie
        {
            Name = ".AspNetCore.Culture", Value = $"c={culture}|uic={culture}", Url = AspireFixture.WebBaseUri.ToString()
        }]);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        const string NoParameters = "e2e-ext-no-parameters";
        var entries = await ApiClient.GetCustomTasksAsync();
        while (new[] { "e2e-probe", NoParameters }.Any(type => entries.All(e => e.TaskType != type)) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            entries = await ApiClient.GetCustomTasksAsync();
        }
        var probe = entries.Single(e => e.TaskType == "e2e-probe");
        var ru = culture == "ru";
        var catalog = new CustomTaskCatalogPage(Page);
        await catalog.OpenAsync();
        foreach (var heading in ru ? new[] { "Тип задачи", "Название", "Узлы / воркеры", "Параметры" } : new[] { "Task Type", "Display Name", "Silos / workers", "Parameters" })
            await Expect(catalog.Grid.GetByRole(AriaRole.Columnheader, new() { Name = heading, Exact = true })).ToBeVisibleAsync();
        await Expect(catalog.Cells(probe.TaskType)).ToHaveTextAsync([
            probe.TaskType, probe.DisplayName ?? "—", "—", ru ? "Публичный" : "Public", "—", string.Join(", ", probe.SiloNames), "—",
            (probe.ParameterSchema?.Parameters.Count ?? 0).ToString("N0", CultureInfo.GetCultureInfo(culture))
        ]);

        foreach (var width in new[] { 768, 1280, 1920 })
        {
            await Page.SetViewportSizeAsync(width, 900);
            await Page.WaitForFunctionAsync("""
                () => {
                    const host = document.querySelector('.custom-task-catalog-grid-container');
                    const grid = host.querySelector('table');
                    const style = getComputedStyle(host.parentElement);
                    const available = host.parentElement.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
                    const titles = [...grid.querySelectorAll('thead .col-title-text')];
                    return Math.abs(host.getBoundingClientRect().width - available) < 2 &&
                        grid.getBoundingClientRect().width >= host.clientWidth - 1 &&
                        titles.length === 8 && titles.every(title => title.scrollWidth <= title.clientWidth + 1);
                }
                """);
            if (ru && width == 1280)
            {
                var path = Path.Combine(TestContext.TestRunResultsDirectory!, $"custom-tasks-ru-{Guid.NewGuid():N}.png");
                await Page.ScreenshotAsync(new() { Path = path, FullPage = true });
                TestContext.AddResultFile(path);
                Console.WriteLine($"Localization screenshot: {path}");
            }
        }
        await Page.GetByRole(AriaRole.Button, new() { Name = ru ? "Тип задачи" : "Task Type", Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = ru ? "Увеличить ширину столбца" : "Widen column", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = ru ? "Сбросить ширину столбцов" : "Reset column widths", Exact = true })).ToBeVisibleAsync();

        var bare = entries.Single(e => e.TaskType == NoParameters);
        await CustomTaskLocalization.OpenServiceTaskAsync(Page, bare.TaskType);
        await Expect(Page.GetByText(ru ? "У этого плагина нет параметров." : "This plugin has no parameters.", new() { Exact = true })).ToBeVisibleAsync();
        await Expect(Page.Locator("fluent-option", new() { HasTextString = $"{bare.DisplayName} ({bare.TaskType})" })).ToHaveCountAsync(1);
        var xml = await new EditorPage(Page).GetXmlAsync();
        StringAssert.Contains(xml, $"type=\"{bare.TaskType}\"");
        await Expect(Page.GetByText(ru ? "Выходные данные задаются вручную:" : "Outputs are free-form", new() { Exact = false })).ToBeVisibleAsync();
    }
}

internal static class CustomTaskLocalization
{
    public static async Task OpenServiceTaskAsync(IPage page, string taskType, string? processKey = null)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <definitions xmlns="http://www.omg.org/spec/BPMN/20100524/MODEL"
                         xmlns:zeebe="http://camunda.org/schema/zeebe/1.0"
                         id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn">
              <process id="{processKey ?? $"localization-{InlineBpmn.UniqueSuffix()}"}" isExecutable="true">
                <startEvent id="start" />
                <serviceTask id="custom"><extensionElements><zeebe:taskDefinition type="{taskType}" /></extensionElements></serviceTask>
                <endEvent id="end" />
                <sequenceFlow id="f1" sourceRef="start" targetRef="custom" />
                <sequenceFlow id="f2" sourceRef="custom" targetRef="end" />
              </process>
            </definitions>
            """;
        var editor = new EditorPage(page);
        await editor.OpenAsync();
        await editor.LoadXmlAsync(xml);
        // Selection through the modeler avoids onboarding overlays intercepting a canvas click.
        await page.EvaluateAsync("""
            () => {
                const modeler = window.bpmnEditor._modeler;
                modeler.get('selection').select(modeler.get('elementRegistry').get('custom'));
            }
            """);
    }
}
