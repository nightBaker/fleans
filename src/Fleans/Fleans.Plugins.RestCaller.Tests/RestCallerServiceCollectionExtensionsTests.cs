using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Plugins.RestCaller.Tests;

[TestClass]
public class RestCallerServiceCollectionExtensionsTests
{
    private static long ResolvedBufferSize(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(RestCallerHandler));
        return client.MaxResponseContentBufferSize;
    }

    [TestMethod]
    public void AddRestCallerPlugin_CapsResponseBufferAtDefault()
    {
        var services = new ServiceCollection();
        services.AddRestCallerPlugin();

        Assert.AreEqual(RestCallerServiceCollectionExtensions.DefaultMaxResponseContentBufferSize,
            ResolvedBufferSize(services));
    }

    [TestMethod]
    public void TypedClientRegisteredAfterPlugin_OverridesBufferCap()
    {
        // HttpClient configure actions run in registration order, so the override must come
        // AFTER AddRestCallerPlugin — registered before, the plugin's 8 MiB would win.
        const long raised = 64L * 1024 * 1024;
        var services = new ServiceCollection();
        services.AddRestCallerPlugin();
        services.AddHttpClient<RestCallerHandler>(c => c.MaxResponseContentBufferSize = raised);

        Assert.AreEqual(raised, ResolvedBufferSize(services));
    }

    [TestMethod]
    public void TypedClientRegisteredBeforePlugin_IsOverwrittenByDefault()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<RestCallerHandler>(c => c.MaxResponseContentBufferSize = 64L * 1024 * 1024);
        services.AddRestCallerPlugin();

        Assert.AreEqual(RestCallerServiceCollectionExtensions.DefaultMaxResponseContentBufferSize,
            ResolvedBufferSize(services));
    }
}
