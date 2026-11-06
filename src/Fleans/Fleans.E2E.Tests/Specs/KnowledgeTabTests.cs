using Fleans.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Microsoft.Playwright.Assertions;

namespace Fleans.E2E.Tests.Specs;

// #1025 — the optional Knowledge tab against the stub backend's canned index (TestHttpServer). Token
// forwarding is in AuthenticationTests; capability rules and Markdown safety also in Fleans.Web.Tests.
[TestClass]
[TestCategory("E2E")]
public class KnowledgeTabTests : WorkflowE2ETestBase
{
    [TestCleanup]
    public void BackendWithoutKnowledge()
    {
        TestHttpServer.AssistantHasKnowledge = false;
        TestHttpServer.KnowledgeIsEmpty = false;
    }

    private async Task OpenAsync()
    {
        TestHttpServer.AssistantHasKnowledge = true;
        await Page.GotoAsync("/knowledge");
        await Expect(Page.GetByTestId("knowledge-sources")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    private async Task SearchAsync(string query)
    {
        await Page.GetByTestId("knowledge-tab-search").ClickAsync();
        await Page.GetByTestId("knowledge-search-input").Locator("input").FillAsync(query);
        await Page.GetByTestId("knowledge-search-submit").ClickAsync();
    }

    [TestMethod]
    public async Task WithoutTheCapability_NoTab_AndTheRouteIsNotFound()
    {
        // The stub is a v1 backend: 404 on capabilities.
        await Page.GotoAsync("/knowledge");

        await Expect(Page.GetByText("the content you are looking for does not exist")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.GetByTestId("nav-knowledge")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("knowledge-sources")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
    }

    [TestMethod]
    public async Task NothingIndexed_SaysNotIndexedYet()
    {
        TestHttpServer.AssistantHasKnowledge = true;
        TestHttpServer.KnowledgeIsEmpty = true;

        await Page.GotoAsync("/knowledge");

        await Expect(Page.GetByTestId("knowledge-not-indexed")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.GetByTestId("nav-knowledge")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task Sources_ListRepositories_AndFilterByProject()
    {
        await OpenAsync();
        await Expect(Page.GetByTestId("nav-knowledge")).ToBeVisibleAsync(new() { Timeout = 30_000 });

        var row = Page.GetByTestId("knowledge-source-bank/payments");
        await Expect(row).ToContainTextAsync("payments");
        await Expect(row).ToContainTextAsync("main");
        await Expect(row).ToContainTextAsync("abcdef12");
        await Expect(row).ToContainTextAsync("adr 2");
        await Expect(row).ToContainTextAsync("on");
        await Expect(Page.GetByTestId("knowledge-source-bank/ops")).ToBeVisibleAsync();

        var project = Page.GetByTestId("knowledge-project");
        await project.ClickAsync();
        await project.Locator("fluent-option[value='ops']").ClickAsync();

        await Expect(Page.GetByTestId("knowledge-source-bank/payments")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("knowledge-source-bank/ops")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task Search_ShowsHits_FiltersByKind_AndAHitOpensItsChunks()
    {
        await OpenAsync();

        await SearchAsync("retry policy");

        var hits = Page.GetByTestId("knowledge-hit");
        await Expect(hits).ToHaveCountAsync(3);
        var adr = hits.Filter(new() { HasText = "bank/payments:docs/adr/0001-retry.md:12" });
        await Expect(adr).ToContainTextAsync("main @ abcdef12");
        await Expect(adr).ToContainTextAsync("Retry policy > Decision");
        await Expect(adr.GetByTestId("knowledge-git-link")).ToHaveAttributeAsync("href",
            "https://git.test/bank/payments/blob/abcdef1234567890/docs/adr/0001-retry.md#L12");
        await Expect(hits.Filter(new() { HasText = "Bank.PayService" })).ToBeVisibleAsync();

        await Page.GetByTestId("knowledge-kind-code").ClickAsync();
        await Expect(hits).ToHaveCountAsync(1);
        await Page.GetByTestId("knowledge-kind-code").ClickAsync();
        await Expect(hits).ToHaveCountAsync(3);

        await adr.GetByTestId("knowledge-hit-open").ClickAsync();

        // Line order, Markdown rendered, and the repository's script and link neutralized.
        var chunks = Page.GetByTestId("knowledge-chunk");
        await Expect(chunks).ToHaveCountAsync(2);
        await Expect(chunks.Nth(0).Locator("h1")).ToHaveTextAsync("Retry policy");
        await Expect(chunks.Nth(1).Locator("strong").Last).ToHaveTextAsync("backoff");
        await Expect(Page.Locator(".knowledge-chunk--target")).ToContainTextAsync("Decision");
        await Expect(Page.GetByTestId("knowledge-markdown").Locator("script, a")).ToHaveCountAsync(0);
        Assert.IsTrue(await Page.EvaluateAsync<bool>("() => window.__xss === undefined"));

        // Back returns to the search as it was.
        await Page.GetByTestId("knowledge-back").ClickAsync();
        await Expect(hits).ToHaveCountAsync(3);
        await Expect(Page.GetByTestId("knowledge-search-input").Locator("input")).ToHaveValueAsync("retry policy");
    }

    [TestMethod]
    public async Task Search_WithoutHits_SaysSo()
    {
        await OpenAsync();

        await SearchAsync("nothing");

        await Expect(Page.GetByTestId("knowledge-no-hits")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task Browse_BuildsTheTree_PagesThroughFiles_AndOpensEachKind()
    {
        await OpenAsync();
        await Page.GetByTestId("knowledge-tab-browse").ClickAsync();

        // The first page only, until asked for more.
        var tree = Page.GetByTestId("knowledge-tree");
        await Expect(tree.GetByTestId("knowledge-dir-src")).ToBeVisibleAsync();
        await Expect(tree.GetByTestId("knowledge-dir-flows")).ToHaveCountAsync(0);
        await tree.GetByTestId("knowledge-dir-src").ClickAsync();
        var file = tree.GetByTestId("knowledge-file").Filter(new() { HasText = "Pay.cs" });
        await Expect(file).ToBeVisibleAsync();

        await Page.GetByTestId("knowledge-load-more").ClickAsync();
        await Expect(tree.GetByTestId("knowledge-dir-flows")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("knowledge-load-more")).ToHaveCountAsync(0);

        // The next page inserted a directory before src: src stays open, and flows stays closed.
        await Expect(file).ToBeVisibleAsync();
        await Expect(tree.GetByTestId("knowledge-file").Filter(new() { HasText = "pay.bpmn" })).ToBeHiddenAsync();

        await Expect(file.GetByTestId("knowledge-git-link")).ToHaveAttributeAsync("href",
            "https://git.test/bank/payments/blob/abcdef1234567890/src/Pay.cs");
        await file.GetByTestId("knowledge-file-open").ClickAsync();
        await Expect(Page.GetByTestId("knowledge-code")).ToContainTextAsync("public class PayService");
        await Expect(Page.GetByTestId("knowledge-chunk").GetByTestId("knowledge-git-link")).ToHaveAttributeAsync("href",
            "https://git.test/bank/payments/blob/abcdef1234567890/src/Pay.cs#L5");
        await Expect(Page.GetByTestId("knowledge-truncated")).ToHaveCountAsync(0);
        await Page.GetByTestId("knowledge-back").ClickAsync();

        await tree.GetByTestId("knowledge-dir-flows").ClickAsync();
        await tree.GetByTestId("knowledge-file").Filter(new() { HasText = "pay.bpmn" }).GetByTestId("knowledge-file-open").ClickAsync();
        await Expect(Page.GetByTestId("knowledge-summary")).ToContainTextAsync("uses rest-call");
        await Expect(Page.GetByTestId("knowledge-truncated")).ToBeVisibleAsync();
    }

    [TestMethod]
    public async Task Browse_APathTheBackendAnswers404For_ShowsNotFound_NotAnErrorPage()
    {
        await OpenAsync();
        await Page.GetByTestId("knowledge-tab-browse").ClickAsync();
        await Page.GetByTestId("knowledge-load-more").ClickAsync();
        var tree = Page.GetByTestId("knowledge-tree");
        await tree.GetByTestId("knowledge-dir-docs").ClickAsync();

        await tree.GetByTestId("knowledge-file").Filter(new() { HasText = "gone.md" }).GetByTestId("knowledge-file-open").ClickAsync();

        await Expect(Page.GetByTestId("knowledge-file-view").GetByTestId("knowledge-not-found")).ToContainTextAsync("Not found");
        await Expect(Page.GetByTestId("knowledge-error")).ToHaveCountAsync(0);
        await Expect(Page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
    }

    [TestMethod]
    public async Task Adrs_ListEachFileOnce_AndSearch()
    {
        await OpenAsync();

        await Page.GetByTestId("knowledge-tab-adrs").ClickAsync();

        var hits = Page.GetByTestId("knowledge-hit");
        await Expect(hits).ToHaveCountAsync(1);
        await Expect(hits).ToContainTextAsync("docs/adr/0001-retry.md");

        await Page.GetByTestId("knowledge-adrs-input").Locator("input").FillAsync("nothing");
        await Page.GetByTestId("knowledge-adrs-input").Locator("input").PressAsync("Enter");
        await Expect(Page.GetByTestId("knowledge-no-adrs")).ToBeVisibleAsync();
    }

    [TestMethod]
    [DataRow(1280, 720)]
    [DataRow(768, 800)]
    [DataRow(390, 844)]
    public async Task ExtraNavEntry_DoesNotOverflowThePage_InEitherLanguage(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Context.AddCookiesAsync([new Cookie
        {
            Name = ".AspNetCore.Culture", Value = "c=ru|uic=ru", Url = WebBase.ToString(),
        }]);

        await OpenAsync();

        await Expect(Page.GetByTestId("nav-knowledge")).ToHaveTextAsync("База знаний");
        var overflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
        Assert.IsFalse(overflow, "the document must not scroll sideways");
    }
}
