namespace Fleans.Application.Placement;

/// <summary>
/// Picks the integer key for a call to a stateless worker grain that runs on worker silos
/// (<c>IScriptExecutorGrain</c>, <c>IConditionExpressionEvaluatorGrain</c>).
/// </summary>
/// <remarks>
/// A silo that cannot host the grain (a <c>core-</c> silo) places a stateless worker on one
/// random worker silo and caches that choice per grain id. With a single fixed key every
/// core silo would pin all its calls to one worker silo, so extra worker silos would add no
/// capacity. Spreading calls over a small key range gives each core silo several cached
/// placements across the worker silos. On a worker or combined silo every key places locally.
/// </remarks>
public static class WorkerGrainKey
{
    /// <summary>Number of distinct keys; well above any expected worker-silo count.</summary>
    public const int Count = 16;

    public static long Next() => Random.Shared.Next(Count);
}
