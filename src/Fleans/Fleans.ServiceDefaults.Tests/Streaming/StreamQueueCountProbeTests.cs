using System.Net;
using Fleans.ServiceDefaults.Streaming;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Orleans;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Tests.Streaming;

[TestClass]
public sealed class StreamQueueCountProbeTests
{
    private static SiloAddress NewSilo(int port) =>
        SiloAddress.New(IPAddress.Loopback, port, generation: 0);

    private static MembershipEntry Host(SiloAddress address) =>
        new() { SiloAddress = address, SiloName = "silo", Status = SiloStatus.Active };

    [TestMethod]
    public async Task HomogeneousCluster_NoWarningLogged()
    {
        var local = NewSilo(11111);
        var peer = NewSilo(22222);
        var logger = new CapturingLogger<StreamQueueCountProbe>();

        IReadOnlyList<StreamQueueCountEntry> entries =
        [
            new(local.ToString(), "StreamProvider", 8),
            new(peer.ToString(), "StreamProvider", 8),
        ];

        var registry = Substitute.For<IStreamQueueCountRegistryGrain>();
        registry.RegisterAsync(Arg.Any<StreamQueueCountEntry>()).Returns(Task.CompletedTask);
        registry.GetEntriesAsync("StreamProvider").Returns(Task.FromResult(entries));

        var management = Substitute.For<IManagementGrain>();
        management.GetDetailedHosts(onlyActive: true).Returns(
            Task.FromResult(new[] { Host(local), Host(peer) }));

        var siloDetails = Substitute.For<ILocalSiloDetails>();
        siloDetails.SiloAddress.Returns(local);

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IStreamQueueCountRegistryGrain>(0, null).Returns(registry);
        grainFactory.GetGrain<IManagementGrain>(0, null).Returns(management);

        var probe = new StreamQueueCountProbe("StreamProvider", 8, grainFactory, siloDetails, logger);
        await probe.RunProbeAsync(CancellationToken.None);

        Assert.AreEqual(0, logger.Entries.Count(e => e.EventId.Id == 11300));
    }

    [TestMethod]
    public async Task HeterogeneousPeer_WarnsWithEventId11300()
    {
        var local = NewSilo(11111);
        var peer = NewSilo(22222);
        var logger = new CapturingLogger<StreamQueueCountProbe>();

        IReadOnlyList<StreamQueueCountEntry> entries =
        [
            new(local.ToString(), "StreamProvider", 8),
            new(peer.ToString(), "StreamProvider", 4),
        ];

        var registry = Substitute.For<IStreamQueueCountRegistryGrain>();
        registry.RegisterAsync(Arg.Any<StreamQueueCountEntry>()).Returns(Task.CompletedTask);
        registry.GetEntriesAsync("StreamProvider").Returns(Task.FromResult(entries));

        var management = Substitute.For<IManagementGrain>();
        management.GetDetailedHosts(onlyActive: true).Returns(
            Task.FromResult(new[] { Host(local), Host(peer) }));

        var siloDetails = Substitute.For<ILocalSiloDetails>();
        siloDetails.SiloAddress.Returns(local);

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IStreamQueueCountRegistryGrain>(0, null).Returns(registry);
        grainFactory.GetGrain<IManagementGrain>(0, null).Returns(management);

        var probe = new StreamQueueCountProbe("StreamProvider", 8, grainFactory, siloDetails, logger);
        await probe.RunProbeAsync(CancellationToken.None);

        Assert.AreEqual(1, logger.Entries.Count(e => e.EventId.Id == 11300 && e.Level == LogLevel.Warning));
    }

    [TestMethod]
    public async Task RegistryGrainThrows_ProbeLogsErrorAndReturns()
    {
        var local = NewSilo(11111);
        var logger = new CapturingLogger<StreamQueueCountProbe>();

        var registry = Substitute.For<IStreamQueueCountRegistryGrain>();
        registry.RegisterAsync(Arg.Any<StreamQueueCountEntry>()).Returns(
            Task.FromException(new InvalidOperationException("network error")));

        var siloDetails = Substitute.For<ILocalSiloDetails>();
        siloDetails.SiloAddress.Returns(local);

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IStreamQueueCountRegistryGrain>(0, null).Returns(registry);

        var probe = new StreamQueueCountProbe("StreamProvider", 8, grainFactory, siloDetails, logger);
        await probe.RunProbeAsync(CancellationToken.None); // must not throw

        Assert.AreEqual(1, logger.Entries.Count(e => e.EventId.Id == 11301 && e.Level == LogLevel.Error));
    }

    [TestMethod]
    public async Task DeadSiloRegistration_FilteredByManagementGrain_NoFalsePositive()
    {
        var local = NewSilo(11111);
        var dead = NewSilo(33333);
        var logger = new CapturingLogger<StreamQueueCountProbe>();

        IReadOnlyList<StreamQueueCountEntry> entries =
        [
            new(local.ToString(), "StreamProvider", 8),
            new(dead.ToString(), "StreamProvider", 4),
        ];

        var registry = Substitute.For<IStreamQueueCountRegistryGrain>();
        registry.RegisterAsync(Arg.Any<StreamQueueCountEntry>()).Returns(Task.CompletedTask);
        registry.GetEntriesAsync("StreamProvider").Returns(Task.FromResult(entries));

        var management = Substitute.For<IManagementGrain>();
        management.GetDetailedHosts(onlyActive: true).Returns(
            Task.FromResult(new[] { Host(local) })); // dead silo not returned

        var siloDetails = Substitute.For<ILocalSiloDetails>();
        siloDetails.SiloAddress.Returns(local);

        var grainFactory = Substitute.For<IGrainFactory>();
        grainFactory.GetGrain<IStreamQueueCountRegistryGrain>(0, null).Returns(registry);
        grainFactory.GetGrain<IManagementGrain>(0, null).Returns(management);

        var probe = new StreamQueueCountProbe("StreamProvider", 8, grainFactory, siloDetails, logger);
        await probe.RunProbeAsync(CancellationToken.None);

        Assert.AreEqual(0, logger.Entries.Count(e => e.EventId.Id == 11300));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId)> Entries { get; } = [];

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, eventId));

        bool ILogger.IsEnabled(LogLevel logLevel) => true;
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
    }
}
