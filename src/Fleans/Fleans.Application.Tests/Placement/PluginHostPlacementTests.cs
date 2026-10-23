using System.Reflection;
using Fleans.Application.Placement;
using Fleans.Worker.CustomTasks;
using Fleans.Worker.Hosting;
using Fleans.Worker.Placement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime.Placement;

namespace Fleans.Application.Tests.Placement;

[TestClass]
public class PluginHostPlacementTests
{
    [TestMethod]
    public void CustomTaskHandlerBase_ShouldNotCarry_WorkerPlacementAttribute()
    {
        // Plugin handlers must use Orleans default placement so GetCompatibleSilos
        // (assembly-loading) is the only thing that decides where they activate.
        var attr = typeof(CustomTaskHandlerBase).GetCustomAttribute<WorkerPlacementAttribute>(inherit: false);

        Assert.IsNull(attr,
            "[WorkerPlacement] on CustomTaskHandlerBase causes plugin grains to land on " +
            "worker-/combined- silos that may not have the plugin's DLL loaded. Remove it; " +
            "rely on Orleans' GetCompatibleSilos for per-plugin isolation.");
    }

    [TestMethod]
    public void WorkerPlacementFilterDirector_HasWorkerRole_ShouldRejectPluginPrefix()
    {
        var method = typeof(Fleans.Worker.Placement.WorkerPlacementFilterDirector).GetMethod(
            "HasWorkerRole",
            BindingFlags.NonPublic | BindingFlags.Static);

        if (method is null)
        {
            Assert.Inconclusive(
                "HasWorkerRole is not a private static method on WorkerPlacementFilterDirector. " +
                "Skipping characterization assertion — covered by PluginHostIsolationIntegrationTests in Task 5.");
            return;
        }

        bool worker = (bool)method.Invoke(null, new object?[] { "worker-host1-abc" })!;
        bool combined = (bool)method.Invoke(null, new object?[] { "combined-host1-abc" })!;
        bool plugin = (bool)method.Invoke(null, new object?[] { "plugin-host1-abc" })!;
        bool core = (bool)method.Invoke(null, new object?[] { "core-host1-abc" })!;

        Assert.IsTrue(worker, "worker- prefix must be accepted");
        Assert.IsTrue(combined, "combined- prefix must be accepted");
        Assert.IsFalse(plugin, "plugin- prefix must be rejected by WorkerPlacementFilterDirector");
        Assert.IsFalse(core, "core- prefix must be rejected by WorkerPlacementFilterDirector");
    }

    [TestMethod]
    public void AddFleansPluginHost_RejectsCoreAndWorkerRoles()
    {
        Assert.AreEqual("plugin", Fleans.Worker.Hosting.PluginHostExtensions.ValidatePluginRole("Plugin"));
        Assert.AreEqual("plugin", Fleans.Worker.Hosting.PluginHostExtensions.ValidatePluginRole(null));
        Assert.AreEqual("combined", Fleans.Worker.Hosting.PluginHostExtensions.ValidatePluginRole("Combined"));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => Fleans.Worker.Hosting.PluginHostExtensions.ValidatePluginRole("Worker"));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => Fleans.Worker.Hosting.PluginHostExtensions.ValidatePluginRole("Core"));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => Fleans.Worker.Hosting.PluginHostExtensions.ValidatePluginRole("garbage"));
    }

    [TestMethod]
    public void BuildPluginSiloName_PrefixesWithRole()
    {
        var name = Fleans.Worker.Hosting.PluginHostExtensions.BuildSiloName(
            "plugin",
            "host42",
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        Assert.AreEqual("plugin-host42-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", name);
    }

    [TestMethod]
    public void AddFleansPluginHost_DisablesGatewayPort()
    {
        // #703 regression guard. Plugin host silos must NOT advertise as Orleans client
        // gateways: their assembly load context intentionally lacks Fleans.Application /
        // Fleans.Domain / Fleans.Persistence. An engine grain call (e.g. IWorkflowInstanceGrain)
        // forwarded *through* a plugin gateway throws "Unable to load type" at the gateway.
        // Removing the EndpointOptions.GatewayPort = 0 line from AddFleansPluginHost MUST
        // make this test fail.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fleans:Role"] = "Plugin" })
            .Build();

        var builder = new FakeSiloBuilder(configuration);
        builder.AddFleansPluginHost(configuration);

        using var provider = builder.Services.BuildServiceProvider();
        var endpoints = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Orleans.Configuration.EndpointOptions>>().Value;

        Assert.AreEqual(0, endpoints.GatewayPort,
            "AddFleansPluginHost must set EndpointOptions.GatewayPort = 0 — plugin silos lack " +
            "Fleans.Application and cannot serve as Orleans client gateways for engine grains (#703).");
    }

    [TestMethod]
    public void AddFleansPluginHost_RegistersCoreDirectorAndWorkerFilter()
    {
        // #627 regression guard. Removing the Core director or the Worker filter registration
        // from AddFleansPluginHost MUST make this test fail. Without the Core registration
        // external plugin hosts hit "KeyNotFoundException: Could not resolve placement
        // strategy CorePlacementStrategy" when their plugins route to
        // CustomTaskCatalogGrain ([CorePlacement]) for catalog registration; without the
        // Worker filter Orleans throws KeyNotFoundException when the plugin silo resolves
        // placement for a [WorkerPlacement] grain (#857).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Fleans:Role"] = "Plugin" })
            .Build();

        var builder = new FakeSiloBuilder(configuration);
        builder.AddFleansPluginHost(configuration);

        // Orleans 10's AddPlacementDirector<TStrategy, TDirector> registers IPlacementDirector
        // as a native .NET keyed service keyed by typeof(TStrategy); AddPlacementFilter does the
        // same for IPlacementFilterDirector (keyed by the filter type) and also registers the
        // filter strategy keyed by its type name. Inspect the ServiceCollection directly so the
        // test does not need to materialize the directors.
        var coreDirector = builder.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(IPlacementDirector)
            && d.IsKeyedService
            && Equals(d.ServiceKey, typeof(CorePlacementStrategy)));
        var workerFilterDirector = builder.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(IPlacementFilterDirector)
            && d.IsKeyedService
            && Equals(d.ServiceKey, typeof(WorkerPlacementFilterStrategy)));
        var workerFilterStrategy = builder.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(PlacementFilterStrategy)
            && d.IsKeyedService
            && Equals(d.ServiceKey, nameof(WorkerPlacementFilterStrategy)));

        Assert.IsNotNull(coreDirector,
            "AddFleansPluginHost must register CorePlacementDirector so external plugin hosts " +
            "can route to [CorePlacement] grains like CustomTaskCatalogGrain (#627).");
        Assert.AreEqual(typeof(CorePlacementDirector), coreDirector.KeyedImplementationType,
            "CorePlacementStrategy must resolve to CorePlacementDirector.");
        Assert.IsNotNull(workerFilterDirector,
            "AddFleansPluginHost must register WorkerPlacementFilterDirector for engine worker grains.");
        Assert.AreEqual(typeof(WorkerPlacementFilterDirector), workerFilterDirector.KeyedImplementationType,
            "WorkerPlacementFilterStrategy must resolve to WorkerPlacementFilterDirector.");
        Assert.IsNotNull(workerFilterStrategy,
            "AddFleansPluginHost must register WorkerPlacementFilterStrategy by name so Orleans can resolve it.");
    }

    private sealed class FakeSiloBuilder : ISiloBuilder
    {
        public FakeSiloBuilder(IConfiguration configuration)
        {
            Services = new ServiceCollection();
            Configuration = configuration;
        }

        public IServiceCollection Services { get; }
        public IConfiguration Configuration { get; }
    }
}
