namespace Fleans.Application;

/// <summary>
/// Calls to the worker-side grains — script dispatch to <c>IScriptExecutorGrain</c>, and
/// <c>IConditionExpressionEvaluatorGrain</c> for conditional start events (every other condition
/// is evaluated in the workflow instance) — bound from <c>Fleans:Worker</c>.
/// </summary>
public sealed class WorkerEvaluatorOptions
{
    public const string SectionName = "Fleans:Worker";

    /// <summary>Dispatches of one script task without a result before the task fails.</summary>
    public const int ScriptDispatchAttempts = 3;

    /// <summary>
    /// How many grain keys callers spread evaluator calls over. The evaluators are stateless
    /// workers restricted to worker silos; a <c>core-</c> silo places each key on one random
    /// worker silo and keeps that choice, so a single key would pin all of that silo's calls to
    /// one worker. Keep it well above the number of worker silos so calls reach all of them;
    /// a larger value only adds idle activations. On a worker or combined silo every key runs
    /// locally. Default 16.
    /// </summary>
    public int EvaluatorKeyCount { get; set; } = 16;

    /// <summary>
    /// How long a workflow instance waits for the result of a script it dispatched before it
    /// dispatches the script again (the dispatch or its result may have been lost with a silo).
    /// After <see cref="ScriptDispatchAttempts"/> dispatches without a result the script task
    /// fails with a <see cref="TimeoutException"/>. Keep it above the script timeout (10 s) plus
    /// the executor's callback retries. Default 30 s.
    /// </summary>
    public TimeSpan ScriptResultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Picks the key for the next evaluator call.</summary>
    public long NextKey() => Random.Shared.Next(EvaluatorKeyCount);
}
