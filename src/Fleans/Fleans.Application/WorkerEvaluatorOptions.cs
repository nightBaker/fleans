namespace Fleans.Application;

/// <summary>
/// Spread of calls to the script and condition evaluator grains
/// (<c>IScriptExecutorGrain</c>, <c>IConditionExpressionEvaluatorGrain</c>), bound from
/// <c>Fleans:Worker</c>.
/// </summary>
public sealed class WorkerEvaluatorOptions
{
    public const string SectionName = "Fleans:Worker";

    /// <summary>
    /// How many grain keys callers spread evaluator calls over. The evaluators are stateless
    /// workers restricted to worker silos; a <c>core-</c> silo places each key on one random
    /// worker silo and keeps that choice, so a single key would pin all of that silo's calls to
    /// one worker. Keep it well above the number of worker silos so calls reach all of them;
    /// a larger value only adds idle activations. On a worker or combined silo every key runs
    /// locally. Default 16.
    /// </summary>
    public int EvaluatorKeyCount { get; set; } = 16;

    /// <summary>Picks the key for the next evaluator call.</summary>
    public long NextKey() => Random.Shared.Next(EvaluatorKeyCount);
}
