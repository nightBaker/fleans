using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Fleans.ServiceDefaults.Clustering;

/// <summary>
/// Works around an Orleans 10.4 membership bug that misroutes reminder registrations (#902).
/// <para>
/// A silo that starts while the membership table is still empty (every silo of a fresh cluster
/// but the first to write) begins with a local <c>Created</c> entry that the empty read cannot
/// replace. When its next read finds another silo but not itself yet, Orleans reports the local
/// silo as <c>Dead</c> to its own status listeners — a few milliseconds before it writes itself
/// as <c>Joining</c>. The consistent ring treats that as "this silo is stopping" and excludes the
/// silo from routing for the rest of its life. Every grain-service call that silo makes for a key
/// it owns itself — above all <c>RegisterOrUpdateReminder</c> from a <c>TimerCallbackGrain</c> —
/// then goes to a neighbour, and since 10.4 a neighbour only stores such a reminder; the owner
/// loads it at its next <c>RefreshReminderListPeriod</c> (5 min), so the BPMN timer fires minutes late.
/// </para>
/// <para>
/// The guard gives the ring a view of membership that drops a termination notification about the
/// local silo while the local silo is not itself terminating. A real shutdown or self-kill publishes
/// the terminating status first, so it still reaches the ring. Only the ring's view changes; the
/// rest of the runtime sees the unmodified oracle.
/// </para>
/// </summary>
public static partial class ConsistentRingSelfStatusGuard
{
    // Internal to Orleans.Runtime, so it is matched by name. Pinned by
    // ConsistentRingSelfStatusGuardTests — an Orleans upgrade that renames it fails startup and the test.
    private const string RingProviderTypeName = "Orleans.Runtime.ConsistentRing.IConsistentRingProvider";

    /// <summary>
    /// Wraps Orleans' consistent-ring registration so the ring ignores a false "local silo is dead"
    /// notification (#902). Called by <see cref="FleansClusteringExtensions.AddFleansClustering(ISiloBuilder, Microsoft.Extensions.Configuration.IConfiguration)"/>.
    /// Throws when the registration is not where Orleans 10.4 puts it.
    /// </summary>
    public static ISiloBuilder AddConsistentRingSelfStatusGuard(this ISiloBuilder siloBuilder)
    {
        var services = siloBuilder.Services;
        var descriptor = services.LastOrDefault(d => d.ServiceType.FullName == RingProviderTypeName);
        if (descriptor is null || descriptor.ImplementationFactory is not { } createRing)
            throw new InvalidOperationException(
                $"Orleans no longer registers {RingProviderTypeName} through a factory, so the #902 ring guard " +
                "cannot be applied. Check whether the Orleans upgrade fixed the self-Dead notification and " +
                "remove the guard, or adapt it (see docs/conventions/clustering.md).");

        if (createRing.Target is GuardedRingFactory)
            return siloBuilder; // already guarded

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Singleton(descriptor.ServiceType, new GuardedRingFactory(createRing).Create));
        return siloBuilder;
    }

    private sealed class GuardedRingFactory(Func<IServiceProvider, object> createRing)
    {
        public object Create(IServiceProvider services) => createRing(new RingServices(services));
    }

    /// <summary>The service provider the ring factory sees: the silo's, with a filtered oracle.</summary>
    private sealed class RingServices(IServiceProvider inner) : IServiceProvider
    {
        private SelfStatusFilteringOracle? _oracle;

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISiloStatusOracle)
                ? _oracle ??= new SelfStatusFilteringOracle(
                    inner.GetRequiredService<ISiloStatusOracle>(),
                    inner.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ConsistentRingSelfStatusGuard)))
                : inner.GetService(serviceType);
    }

    private sealed class SelfStatusFilteringOracle(ISiloStatusOracle inner, ILogger logger) : ISiloStatusOracle
    {
        // Orleans keeps only weak references to listeners, so each wrapper lives exactly as long as
        // the listener it wraps.
        private readonly ConditionalWeakTable<ISiloStatusListener, FilteringListener> _wrappers = new();

        public SiloStatus CurrentStatus => inner.CurrentStatus;
        public string SiloName => inner.SiloName;
        public SiloAddress SiloAddress => inner.SiloAddress;
        public SiloAddress[] GetActiveSilos() => inner.GetActiveSilos();
        public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress) => inner.GetApproximateSiloStatus(siloAddress);
        public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false) =>
            inner.GetApproximateSiloStatuses(onlyActive);
        public bool TryGetSiloName(SiloAddress siloAddress, [NotNullWhen(true)] out string? siloName) =>
            inner.TryGetSiloName(siloAddress, out siloName);
        public bool IsFunctionalDirectory(SiloAddress siloAddress) => inner.IsFunctionalDirectory(siloAddress);
        public bool IsDeadSilo(SiloAddress silo) => inner.IsDeadSilo(silo);

        public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) =>
            inner.SubscribeToSiloStatusEvents(_wrappers.GetValue(observer, o => new FilteringListener(o, this)));

        public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) =>
            _wrappers.TryGetValue(observer, out var wrapper) && inner.UnSubscribeFromSiloStatusEvents(wrapper);

        private sealed class FilteringListener(ISiloStatusListener target, SelfStatusFilteringOracle oracle)
            : ISiloStatusListener
        {
            public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
            {
                var localStatus = oracle.CurrentStatus;
                if (status.IsTerminating() && !localStatus.IsTerminating() && updatedSilo.Equals(oracle.SiloAddress))
                {
                    LogIgnoredSelfTermination(oracle.Logger, updatedSilo, status, localStatus);
                    return;
                }

                target.SiloStatusChangeNotification(updatedSilo, status);
            }
        }

        private ILogger Logger { get; } = logger;
    }

    [LoggerMessage(EventId = 11400, Level = LogLevel.Warning,
        Message = "Consistent ring: ignored a '{ReportedStatus}' notification about the local silo {Silo} while it is " +
                  "'{LocalStatus}' — Orleans reports a joining silo of a fresh cluster as dead to itself; without this " +
                  "guard the silo would stop routing grain-service calls to itself (#902).")]
    private static partial void LogIgnoredSelfTermination(ILogger logger, SiloAddress silo, SiloStatus reportedStatus, SiloStatus localStatus);
}
