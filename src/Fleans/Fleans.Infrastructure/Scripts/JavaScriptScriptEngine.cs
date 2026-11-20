using System.Collections.Concurrent;
using System.Dynamic;
using Acornima.Ast;
using Fleans.Application.Scripts;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using JsJsonParser = Jint.Native.Json.JsonParser;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Fleans.Infrastructure.Scripts;

/// <summary>
/// <c>scriptFormat="javascript"</c> (or <c>js</c>): an ECMAScript script run by Jint, an interpreter
/// with no .NET access (#1070). The variables reach the script as the plain object <c>_context</c>,
/// converted through JSON both ways, so a script sees and returns JSON values only: dates come back
/// as ISO strings, which Newtonsoft parses back to <see cref="DateTime"/>.
/// Unlike DynamicExpresso, Jint stops a script that runs past the timeout or allocates past the
/// memory limit, so a runaway script does not keep a thread.
/// </summary>
public sealed class JavaScriptScriptEngine : IScriptEngine
{
    private static readonly string[] SupportedFormats = ["javascript", "js"];
    private const long MemoryLimitBytes = 64 * 1024 * 1024;
    private const int RecursionLimit = 256;

    private static readonly JsonSerializerSettings VariableJson = new()
    {
        Converters = { new ExpandoObjectConverter() },
    };

    private readonly TimeSpan _scriptTimeout;

    // Parsed scripts, keyed by script text. A prepared script is immutable and shared by the
    // per-run engines. Unbounded: scripts come from deployed process definitions.
    private readonly ConcurrentDictionary<string, Prepared<Script>> _scriptCache = new();

    public JavaScriptScriptEngine()
        : this(TimeSpan.FromSeconds(10))
    {
    }

    public JavaScriptScriptEngine(TimeSpan scriptTimeout)
    {
        _scriptTimeout = scriptTimeout;
    }

    public IReadOnlyCollection<string> Formats => SupportedFormats;

    public Task<ExpandoObject> Execute(string script, ExpandoObject variables)
    {
        if (string.IsNullOrWhiteSpace(script))
            return Task.FromResult(variables);

        // Jint runs synchronously; keep it off the grain's scheduler thread. The engine's own
        // constraints end the run, so this thread is freed when the timeout expires.
        return Task.Run(() => Run(_scriptCache.GetOrAdd(script, Prepare), variables));
    }

    public void Compile(string script)
    {
        if (!string.IsNullOrWhiteSpace(script))
            _scriptCache.GetOrAdd(script, Prepare);
    }

    internal int CachedScriptCount => _scriptCache.Count;

    private static Prepared<Script> Prepare(string script) => Engine.PrepareScript(script);

    private ExpandoObject Run(Prepared<Script> script, ExpandoObject variables)
    {
        // An Engine is not thread-safe, and a fresh one per run keeps runs from seeing each
        // other's globals.
        var engine = new Engine(options => options
            .TimeoutInterval(_scriptTimeout)
            .LimitMemory(MemoryLimitBytes)
            .LimitRecursion(RecursionLimit));

        engine.SetValue("_context", new JsJsonParser(engine).Parse(JsonConvert.SerializeObject(variables, VariableJson)));
        engine.Execute(script);

        var context = engine.GetValue("_context");
        if (!context.IsObject() || context.IsArray() || context is Function)
            throw new InvalidOperationException("A JavaScript script must leave _context an object.");

        var json = new JsJsonSerializer(engine).Serialize(context).AsString();
        return JsonConvert.DeserializeObject<ExpandoObject>(json, VariableJson) ?? new ExpandoObject();
    }
}
