using System.Dynamic;

namespace Fleans.Application.Scripts;

/// <summary>
/// One script language a <c>&lt;scriptTask&gt;</c> can be written in. Register an implementation
/// as a singleton <see cref="IScriptEngine"/>; <see cref="IScriptExpressionExecutor"/> picks the
/// engine whose <see cref="Formats"/> contains the task's <c>scriptFormat</c> (case-insensitive).
/// Deploy validation and the worker both resolve engines this way, so register the engine on every
/// silo that deploys or runs scripts.
/// </summary>
public interface IScriptEngine
{
    /// <summary><c>scriptFormat</c> values this engine handles, compared case-insensitively.</summary>
    IReadOnlyCollection<string> Formats { get; }

    /// <summary>Runs <paramref name="script"/> against <paramref name="variables"/> (bound as <c>_context</c>) and returns the resulting variables.</summary>
    Task<ExpandoObject> Execute(string script, ExpandoObject variables);

    /// <summary>Compiles <paramref name="script"/> without running it, keeping it for later runs. Throws when it does not compile.</summary>
    void Compile(string script);
}
