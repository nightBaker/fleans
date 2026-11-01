using System.Dynamic;

namespace Fleans.Application.Scripts;

public interface IScriptExpressionExecutor
{
    Task<ExpandoObject> Execute(string script, ExpandoObject variables, string scriptFormat);

    /// <summary>
    /// Compiles <paramref name="script"/> without running it and keeps it for later
    /// <see cref="Execute"/> calls. Throws when it does not compile; deployment calls this so a
    /// broken script rejects the deploy (#975).
    /// </summary>
    void Compile(string script, string scriptFormat);
}
