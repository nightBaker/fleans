using System.Collections.Concurrent;
using System.Dynamic;
using System.Numerics;
using System.Reflection;
using Fleans.Application.Scripts;
using IronPython.Hosting;
using IronPython.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Debugging;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Fleans.Infrastructure.Scripts;

/// <summary>
/// <c>scriptFormat="python"</c> (or <c>py</c>): a Python 3 script run in process by IronPython
/// 3.4 (#1102). The variables reach the script as the dict <c>_context</c>, a JSON copy, and only
/// the top-level keys whose JSON the script changed are written back, exactly like a JavaScript
/// task. There is no standard library: <c>import</c> is limited to <see cref="AllowedModules"/>, and
/// <c>open</c>, <c>exec</c>, <c>eval</c> and the <c>clr</c> / <c>System</c> .NET bridge are not
/// reachable. Each run gets its own builtins and module copies, so runs cannot see each other.
/// <para>
/// The limits are enforced from IronPython's trace hook, which fires on every Python line and
/// call and which a script cannot remove: a run past the timeout or past
/// <see cref="StackBudgetBytes"/> of stack raises on its next line, again and again, so a bare
/// <c>except:</c> cannot keep it alive and the thread is freed. Each run gets its own thread with
/// a large stack: IronPython's own recursion limit is skipped once a function has thrown often
/// (its "light throw" recompilation), so the byte budget is what keeps a recursive script from
/// overflowing the silo's stack. There is no memory limit.
/// </para>
/// </summary>
public sealed class PythonScriptEngine : IScriptEngine
{
    private static readonly string[] SupportedFormats = ["python", "py"];

    /// <summary>Modules a script may import: IronPython's built-in, side-effect-free ones.</summary>
    internal static readonly IReadOnlySet<string> AllowedModules = new HashSet<string>(StringComparer.Ordinal)
    {
        "math", "cmath", "re", "itertools",
    };

    // Builtins that reach files, the console, code compilation or the caller's frames.
    private static readonly HashSet<string> BlockedBuiltins = new(StringComparer.Ordinal)
    {
        "open", "compile", "exec", "eval", "input", "breakpoint", "globals", "locals", "vars",
        "help", "exit", "quit", "copyright", "credits", "license", "__import__", "__loader__", "__spec__",
    };

    private const int RecursionLimit = 256;
    private const int ThreadStackBytes = 16 * 1024 * 1024;
    // The rest of the thread's stack is headroom for IronPython's exception handling, which runs
    // its filters on top of the deepest frame.
    internal const int StackBudgetBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerSettings VariableJson = new()
    {
        Converters = { new ExpandoObjectConverter() },
        FloatFormatHandling = FloatFormatHandling.String,
        DateParseHandling = DateParseHandling.None,
    };

    private readonly TimeSpan _scriptTimeout;
    private readonly ScriptEngine _engine;
    private readonly KeyValuePair<object, object?>[] _builtins;
    private readonly object _import;

    // Compiled scripts, keyed by script text. CompiledCode runs concurrently against separate
    // scopes. Unbounded: scripts come from deployed process definitions.
    private readonly ConcurrentDictionary<string, CompiledCode> _scriptCache = new();

    public PythonScriptEngine()
        : this(TimeSpan.FromSeconds(10))
    {
    }

    public PythonScriptEngine(TimeSpan scriptTimeout)
    {
        _scriptTimeout = scriptTimeout;
        // Tracing stays on for good: toggling it per run would recompile every function.
        _engine = Python.CreateEngine(new Dictionary<string, object>
        {
            ["Tracing"] = true,
            ["RecursionLimit"] = RecursionLimit,
        });
        _engine.Runtime.IO.SetOutput(Stream.Null, System.Text.Encoding.UTF8);
        _engine.Runtime.IO.SetErrorOutput(Stream.Null, System.Text.Encoding.UTF8);
        InstallLimits(_engine);

        var builtins = _engine.GetBuiltinModule();
        _import = builtins.GetVariable("__import__");
        _builtins = builtins.GetItems()
            .Where(item => !BlockedBuiltins.Contains(item.Key))
            .Select(item => new KeyValuePair<object, object?>(item.Key, item.Value))
            .ToArray();
    }

    public IReadOnlyCollection<string> Formats => SupportedFormats;

    public Task<ExpandoObject> Execute(string script, ExpandoObject variables)
    {
        if (string.IsNullOrWhiteSpace(script))
            return Task.FromResult(variables);

        var code = _scriptCache.GetOrAdd(script, Prepare);
        var result = new TaskCompletionSource<ExpandoObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        // A dedicated thread: its stack size is known, so the stack budget is real, and a run
        // never holds a grain scheduler or thread-pool thread.
        var thread = new Thread(() =>
        {
            try
            {
                result.SetResult(Run(code, variables));
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        }, ThreadStackBytes)
        {
            IsBackground = true,
            Name = "fleans-python-script",
        };
        thread.Start();
        return result.Task;
    }

    public void Compile(string script)
    {
        if (!string.IsNullOrWhiteSpace(script))
            _scriptCache.GetOrAdd(script, Prepare);
    }

    internal int CachedScriptCount => _scriptCache.Count;

    private CompiledCode Prepare(string script)
    {
        try
        {
            return _engine.CreateScriptSourceFromString(script, SourceCodeKind.Statements).Compile();
        }
        catch (SyntaxErrorException ex)
        {
            throw new InvalidOperationException($"Python syntax error at line {ex.Line}: {ex.Message}");
        }
    }

    /// <summary>
    /// The script's error as a plain .NET exception. ScriptExecutorGrain sends the exception to
    /// the instance grain, possibly on another silo, and IronPython's own exception types carry
    /// Python objects that Orleans cannot serialize.
    /// </summary>
    private Exception ScriptError(Exception ex)
    {
        switch (ex)
        {
            case TimeoutException:
                return new TimeoutException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The Python script ran past its {_scriptTimeout.TotalSeconds:0.###} s timeout."));
            case InsufficientExecutionStackException:
                return new InsufficientExecutionStackException(ex.Message);
        }

        var operations = _engine.GetService<ExceptionOperations>();
        operations.GetExceptionMessage(ex, out var message, out _);
        // The innermost frame is where the script raised.
        var line = operations.GetStackFrames(ex).FirstOrDefault()?.GetFileLineNumber() ?? 0;
        return new InvalidOperationException(line > 0 ? $"{message} (line {line})" : message);
    }

    private ExpandoObject Run(CompiledCode code, ExpandoObject variables)
    {
        var input = ParseObject(JsonConvert.SerializeObject(variables, VariableJson));
        var context = (PythonDictionary)ToPython(input)!;
        // The input as Python sees it, so a value that only changed by crossing into Python
        // does not look written.
        var before = (JObject)FromPython(context, "_context");

        var scope = _engine.CreateScope();
        scope.SetVariable("__builtins__", CreateBuiltins());
        scope.SetVariable("_context", context);

        RunLimits.Start(_scriptTimeout);
        try
        {
            code.Execute(scope);
        }
        catch (Exception ex)
        {
            throw ScriptError(ex);
        }
        finally
        {
            RunLimits.Stop();
        }

        if (!scope.TryGetVariable("_context", out object? result) || result is not PythonDictionary written)
            throw new InvalidOperationException("A Python script must leave _context a dict.");

        var after = (JObject)FromPython(written, "_context");
        var values = JsonConvert.DeserializeObject<ExpandoObject>(after.ToString(Formatting.None), VariableJson)!;
        var target = (IDictionary<string, object?>)variables;
        var source = (IDictionary<string, object?>)values;
        foreach (var (key, value) in after)
        {
            // Deleted keys are not removed: results merge into the scope like a C# script's.
            if (!before.TryGetValue(key, out var old) || !JToken.DeepEquals(old, value))
                target[key] = source[key];
        }
        return variables;
    }

    /// <summary>Fresh builtins per run, so a script that rebinds one does not change the next run's.</summary>
    private PythonDictionary CreateBuiltins()
    {
        var builtins = new PythonDictionary();
        foreach (var (name, value) in _builtins)
            builtins[name] = value;

        // Modules a run imports are copies, so `math.pi = 3` stays in this run.
        var imported = new Dictionary<string, PythonModule>(StringComparer.Ordinal);
        builtins["__import__"] = new Func<string, object?, object?, object?, int, object>((name, globals, locals, fromList, level) =>
        {
            if (level != 0 || !AllowedModules.Contains(name))
                throw new IronPython.Runtime.Exceptions.ImportException(
                    $"import of '{name}' is not allowed; allowed modules: {string.Join(", ", AllowedModules.Order())}");

            if (!imported.TryGetValue(name, out var module))
            {
                var shared = (PythonModule)_engine.Operations.Invoke(_import, name, globals, locals, fromList, level);
                module = new PythonModule();
                var dict = module.Get__dict__();
                foreach (var (key, value) in shared.Get__dict__())
                    dict[key] = value;
                imported[name] = module;
            }
            return module;
        });
        return builtins;
    }

    private static JObject ParseObject(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static object? ToPython(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                var dict = new PythonDictionary();
                foreach (var (key, value) in obj)
                    dict[key] = ToPython(value!);
                return dict;
            case JArray array:
                var list = new PythonList();
                foreach (var item in array)
                    list.append(ToPython(item));
                return list;
            case JValue value:
                return value.Type switch
                {
                    // Python ints are int or BigInteger in IronPython; a .NET long would be a foreign type.
                    JTokenType.Integer => value.Value is BigInteger big ? big : ToPythonInt(Convert.ToInt64(value.Value)),
                    JTokenType.Float => Convert.ToDouble(value.Value),
                    JTokenType.Null or JTokenType.Undefined => null,
                    _ => value.Value,
                };
            default:
                throw new InvalidOperationException($"Unexpected JSON token {token.Type}.");
        }
    }

    private static object ToPythonInt(long value) =>
        value is >= int.MinValue and <= int.MaxValue ? (int)value : new BigInteger(value);

    private static JToken FromPython(object? value, string path)
    {
        switch (value)
        {
            case null:
                return JValue.CreateNull();
            case string or bool or int or long or double or BigInteger:
                return new JValue(value);
            case PythonDictionary dict:
                var obj = new JObject();
                foreach (var (key, item) in dict)
                {
                    if (key is not string name)
                        throw new InvalidOperationException($"{path} has a non-string key {key}; dict keys must be str.");
                    obj[name] = FromPython(item, $"{path}['{name}']");
                }
                return obj;
            case PythonList or PythonTuple:
                var array = new JArray();
                var index = 0;
                foreach (var item in (System.Collections.IEnumerable)value)
                    array.Add(FromPython(item, $"{path}[{index++}]"));
                return array;
            default:
                throw new InvalidOperationException(
                    $"{path} is a {IronPython.Runtime.Types.PythonType.Get__name__(IronPython.Runtime.Types.DynamicHelpers.GetPythonType(value))}, which a process variable cannot hold: use dict, list, tuple, str, int, float, bool or None.");
        }
    }

    private static void InstallLimits(ScriptEngine engine)
    {
        // The trace pipeline exists because Tracing is on. IronPython keeps it internal; the
        // callback is the only hook that runs on every line and that a script cannot unset.
        var context = (PythonContext)HostingHelpers.GetLanguageContext(engine);
        var property = typeof(PythonContext).GetProperty("TracePipeline", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IronPython no longer exposes PythonContext.TracePipeline; the Python script limits cannot be installed.");
        var pipeline = (ITracePipeline)(property.GetValue(context)
            ?? throw new InvalidOperationException("IronPython created no trace pipeline; the Python script limits cannot be installed."));
        pipeline.TraceCallback = new RunLimits(pipeline.TraceCallback);
    }

    /// <summary>
    /// Wraps IronPython's own trace dispatcher and enforces the current thread's run limits.
    /// It throws only on frame-enter and line events: throwing while an exception is already
    /// being handled would recurse through the exception machinery.
    /// </summary>
    private sealed class RunLimits(ITraceCallback inner) : ITraceCallback
    {
        [ThreadStatic] private static long _deadline;
        [ThreadStatic] private static nint _stackBase;

        public static void Start(TimeSpan timeout)
        {
            _stackBase = StackPosition();
            _deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        }

        public static void Stop() => _deadline = 0;

        public void OnTraceEvent(TraceEventKind kind, string name, string sourceFileName, SourceSpan sourceSpan,
            Func<IDictionary<object, object>> scopeCallback, object payload, object customPayload)
        {
            inner.OnTraceEvent(kind, name, sourceFileName, sourceSpan, scopeCallback, payload, customPayload);

            if (_deadline == 0 || kind is not (TraceEventKind.FrameEnter or TraceEventKind.TracePoint))
                return;
            if (_stackBase - StackPosition() > StackBudgetBytes)
                throw new InsufficientExecutionStackException("The Python script recursed past its stack budget.");
            if (Environment.TickCount64 > _deadline)
                throw new TimeoutException();
        }

        private static unsafe nint StackPosition()
        {
            byte marker = 0;
            return (nint)(&marker);
        }
    }
}
