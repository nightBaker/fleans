using System.Collections.Concurrent;
using System.Dynamic;
using Acornima.Ast;
using Fleans.Application.Scripts;
using Jint;
using Jint.Native;
using JsJsonParser = Jint.Native.Json.JsonParser;
using JsJsonSerializer = Jint.Native.Json.JsonSerializer;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Fleans.Infrastructure.Scripts;

/// <summary>
/// <c>scriptFormat="javascript"</c> (or <c>js</c>): an ECMAScript script run by Jint, an interpreter
/// with no .NET access (#1070). The variables reach the script as the plain object <c>_context</c>,
/// a JSON copy. Only the top-level keys whose JSON the script changed are written back, so a
/// variable the script does not touch keeps its .NET value and type (Guid, decimal, a long above
/// 2^53, DateTime). Written values are read as plain JSON: a string stays a string, even one that
/// looks like a date.
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
        // NaN / Infinity as strings: Jint's JSON parser rejects the bare literals.
        FloatFormatHandling = FloatFormatHandling.String,
        DateParseHandling = DateParseHandling.None,
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
        var serializer = new JsJsonSerializer(engine);

        var context = new JsJsonParser(engine).Parse(JsonConvert.SerializeObject(variables, VariableJson));
        // The input as JavaScript sees it: comparing against this, not the .NET JSON, keeps a value
        // that only changed by crossing into JavaScript (a long above 2^53) from looking written.
        var before = ParseObject(serializer.Serialize(context).AsString());

        engine.SetValue("_context", context);
        engine.Execute(script);

        var result = serializer.Serialize(engine.GetValue("_context"));
        if (!result.IsString() || !result.AsString().TrimStart().StartsWith('{'))
            throw new InvalidOperationException("A JavaScript script must leave _context an object.");

        var json = result.AsString();
        var after = ParseObject(json);
        var written = JsonConvert.DeserializeObject<ExpandoObject>(json, VariableJson)!;
        var target = (IDictionary<string, object?>)variables;
        var source = (IDictionary<string, object?>)written;
        foreach (var (key, value) in after)
        {
            // Deleted keys are not removed: results merge into the scope like a C# script's.
            if (!before.TryGetValue(key, out var old) || !JToken.DeepEquals(old, value))
                target[key] = source[key];
        }
        return variables;
    }

    private static JObject ParseObject(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }
}
