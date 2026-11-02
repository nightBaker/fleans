using System.Collections.Concurrent;
using Fleans.Application.Grains;

namespace Fleans.Application.Tests;

/// <summary>
/// Fails the next signal deliveries to chosen workflow instances before the instance sees them,
/// as a transient grain-call failure would (#742), and counts the deliveries each instance has
/// handled — a broadcast returns before the delivery grains reach the instances (#976).
/// Registered on the <see cref="WorkflowTestBase"/> silo.
/// </summary>
public sealed class SignalDeliveryFaults : IIncomingGrainCallFilter
{
    private static readonly ConcurrentDictionary<Guid, (int Count, Func<Exception> Error)> Faults = new();
    private static readonly ConcurrentDictionary<Guid, int> Delivered = new();

    /// <summary>How many signal deliveries the instance has handled.</summary>
    public static int DeliveredTo(Guid instanceId) => Delivered.GetValueOrDefault(instanceId);

    /// <summary>Fails the next <paramref name="count"/> deliveries to the instance with <paramref name="error"/> (default: a timeout).</summary>
    public static void FailNextDeliveries(Guid instanceId, int count, Func<Exception>? error = null) =>
        Faults[instanceId] = (count, error ?? (() => new TimeoutException("Injected signal delivery timeout")));

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.InterfaceMethod?.Name != nameof(IWorkflowInstanceGrain.HandleSignalDelivery)
            || !context.TargetId.TryGetGuidKey(out var instanceId, out _))
        {
            await context.Invoke();
            return;
        }

        if (TryTakeFault(instanceId, out var error))
            throw error;

        await context.Invoke();
        Delivered.AddOrUpdate(instanceId, 1, (_, n) => n + 1);
    }

    private static bool TryTakeFault(Guid instanceId, out Exception error)
    {
        while (Faults.TryGetValue(instanceId, out var fault))
        {
            var taken = fault.Count > 1
                ? Faults.TryUpdate(instanceId, (fault.Count - 1, fault.Error), fault)
                : Faults.TryRemove(KeyValuePair.Create(instanceId, fault));
            if (taken)
            {
                error = fault.Error();
                return true;
            }
        }

        error = null!;
        return false;
    }
}
