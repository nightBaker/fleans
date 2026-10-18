using Fleans.Application.Grains;
using Fleans.Domain.States;

namespace Fleans.Application.Tests;

/// <summary>
/// Test-only probes into <see cref="WorkflowInstance"/> state. Installed as a grain extension by
/// the test silo only, so these reads are not part of the production grain interface.
/// </summary>
public interface IWorkflowInstanceInspector : IGrainExtension
{
    /// <summary>Transaction Sub-Process outcomes, keyed by the Transaction host's activity instance id.</summary>
    ValueTask<IReadOnlyDictionary<Guid, TransactionOutcomeRecord>> GetTransactionOutcomes();

    /// <summary>Op-keys currently in the durable pending-events queue (PendingOperations). #657.</summary>
    ValueTask<IReadOnlyList<string>> GetPendingOperationKeys();

    /// <summary>Op-keys recorded in the dedup ledger (AppliedOperations). #657.</summary>
    ValueTask<IReadOnlyList<string>> GetAppliedOperationKeys();
}

internal sealed class WorkflowInstanceInspector(IGrainContext context) : IWorkflowInstanceInspector
{
    private WorkflowInstanceState State => ((WorkflowInstance)context.GrainInstance!).InspectionState;

    public ValueTask<IReadOnlyDictionary<Guid, TransactionOutcomeRecord>> GetTransactionOutcomes()
    {
        IReadOnlyDictionary<Guid, TransactionOutcomeRecord> result =
            new Dictionary<Guid, TransactionOutcomeRecord>(State.TransactionOutcomes);
        return ValueTask.FromResult(result);
    }

    public ValueTask<IReadOnlyList<string>> GetPendingOperationKeys()
        => ValueTask.FromResult<IReadOnlyList<string>>(State.PendingOperations.Keys.ToList());

    public ValueTask<IReadOnlyList<string>> GetAppliedOperationKeys()
        => ValueTask.FromResult<IReadOnlyList<string>>(State.AppliedOperations.Keys.ToList());
}

/// <summary>Keeps test call sites reading like grain calls.</summary>
public static class WorkflowInstanceInspectorExtensions
{
    public static ValueTask<IReadOnlyDictionary<Guid, TransactionOutcomeRecord>> GetTransactionOutcomes(
        this IWorkflowInstanceGrain grain)
        => grain.AsReference<IWorkflowInstanceInspector>().GetTransactionOutcomes();

    public static ValueTask<IReadOnlyList<string>> GetPendingOperationKeys(this IWorkflowInstanceGrain grain)
        => grain.AsReference<IWorkflowInstanceInspector>().GetPendingOperationKeys();

    public static ValueTask<IReadOnlyList<string>> GetAppliedOperationKeys(this IWorkflowInstanceGrain grain)
        => grain.AsReference<IWorkflowInstanceInspector>().GetAppliedOperationKeys();
}
