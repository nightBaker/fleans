using System.Dynamic;
using Fleans.ExternalTasks.Sdk;
using Newtonsoft.Json;

namespace Fleans.E2E.ExternalWorker;

/// <summary>
/// Test-only worker for <c>&lt;scriptTask scriptFormat="e2e-json"&gt;</c> (#1105): the engine sends a
/// script task in any format other than C# to <c>fleans.script.&lt;format&gt;</c>, with the script text
/// as the <c>script</c> input and every variable. Here the "script" is a JSON object whose properties
/// are the variables it writes; <c>"$throw": "message"</c> throws instead, like a failing script. It
/// also writes <c>receivedVariables</c>, the sorted names of the variables it was given.
/// </summary>
public sealed class JsonScriptHandler : ExternalTaskHandler
{
    public const string TaskType = "fleans.script.e2e-json";

    public override Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs, ExpandoObject variables,
        ExternalTaskContext context, CancellationToken cancellationToken)
    {
        var script = resolvedInputs.TryGetValue("script", out var value) ? value as string : null;
        var written = JsonConvert.DeserializeObject<Dictionary<string, object?>>(script ?? "{}") ?? [];
        if (written.TryGetValue("$throw", out var message))
            throw new InvalidOperationException(message?.ToString());

        written["receivedVariables"] = string.Join(",", ((IDictionary<string, object?>)variables).Keys.Order(StringComparer.Ordinal));
        return Task.FromResult<IDictionary<string, object?>>(written);
    }
}
