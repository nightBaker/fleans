using Fleans.E2E.Tests.ApiClient;
using Microsoft.Playwright;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.E2E.Tests.Infrastructure;

public abstract class WorkflowE2ETestBase
{
    protected static Uri ApiBase => AspireFixture.ApiBaseUri;

    protected static Uri WebBase => AspireFixture.WebBaseUri;

    protected IBrowserContext Context { get; private set; } = null!;

    protected IPage Page { get; private set; } = null!;

    protected FleansApiClient ApiClient { get; private set; } = null!;

    [TestInitialize]
    public async Task InitializeBrowserContextAsync()
    {
        Context = await AspireFixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = AspireFixture.WebBaseUri.ToString(),
        });
        Page = await Context.NewPageAsync();
        ApiClient = new FleansApiClient(AspireFixture.ApiHttpClient);
    }

    [TestCleanup]
    public async Task DisposeBrowserContextAsync()
    {
        if (Context is not null)
        {
            await Context.CloseAsync();
            await Context.DisposeAsync();
        }
    }
}
