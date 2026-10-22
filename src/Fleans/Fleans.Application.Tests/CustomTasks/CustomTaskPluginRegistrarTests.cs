using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orleans;
using Orleans.Runtime;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class CustomTaskPluginRegistrarTests
{
    [TestMethod]
    public async Task Start_CatalogNotYetInClusterManifest_RetriesInsteadOfAbortingTheSilo()
    {
        // Arrange — a plugin host that reaches Active before any Core silo's manifest arrives:
        // GetGrain itself throws until then (seen in CI as "Could not find an implementation").
        var catalog = Substitute.For<ICustomTaskCatalogGrain>();
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<ICustomTaskCatalogGrain>(0, null).Returns(
            _ => throw new ArgumentException("Could not find an implementation for interface ICustomTaskCatalogGrain"),
            _ => catalog);
        var siloDetails = Substitute.For<ILocalSiloDetails>();
        siloDetails.Name.Returns("plugin-host");
        var registrar = new CustomTaskPluginRegistrar(
            grainFactory,
            siloDetails,
            [new CustomTaskPluginDescriptor("e2e-probe", "Probe", null)],
            NullLogger<CustomTaskPluginRegistrar>.Instance);
        var start = CaptureActiveStageStart(registrar);

        // Act — must not throw: an exception here aborts the silo's start
        await start(CancellationToken.None);

        // Assert
        await catalog.Received(1).Register(Arg.Is<CustomTaskRegistration>(r =>
            r.TaskType == "e2e-probe" && r.SiloName == "plugin-host"));
    }

    [TestMethod]
    public async Task Start_CatalogAlwaysUnavailable_GivesUpWithoutAbortingTheSilo()
    {
        // Arrange
        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<ICustomTaskCatalogGrain>(0, null)
            .Throws(new ArgumentException("Could not find an implementation for interface ICustomTaskCatalogGrain"));
        var registrar = new CustomTaskPluginRegistrar(
            grainFactory,
            Substitute.For<ILocalSiloDetails>(),
            [new CustomTaskPluginDescriptor("e2e-probe", "Probe", null)],
            NullLogger<CustomTaskPluginRegistrar>.Instance);
        var start = CaptureActiveStageStart(registrar);
        using var cts = new CancellationTokenSource();

        // Act — cancelling the first back-off ends the retries, as a silo shutdown would
        var run = start(cts.Token);
        await cts.CancelAsync();
        await run;

        // Assert
        grainFactory.Received().GetGrain<ICustomTaskCatalogGrain>(0, null);
    }

    private static Func<CancellationToken, Task> CaptureActiveStageStart(CustomTaskPluginRegistrar registrar)
    {
        ILifecycleObserver? observer = null;
        var lifecycle = Substitute.For<ISiloLifecycle>();
        lifecycle.Subscribe(Arg.Any<string>(), ServiceLifecycleStage.Active, Arg.Do<ILifecycleObserver>(o => observer = o));
        registrar.Participate(lifecycle);
        Assert.IsNotNull(observer, "the registrar must subscribe at ServiceLifecycleStage.Active");
        return observer.OnStart;
    }
}
