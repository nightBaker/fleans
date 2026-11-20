using IronPython.Compiler;
using IronPython.Compiler.Ast;
using IronPython.Runtime;
using IronPython.Runtime.Operations;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Runtime;

namespace Fleans.Infrastructure.Scripts;

/// <summary>
/// The names a Python script may not touch. Removing builtins is not enough on its own: frames
/// and tracebacks (<c>e.__traceback__.tb_frame.f_builtins</c>), generator frames and
/// <c>object.__subclasses__()</c> lead back to the engine's real builtins and from there to
/// <c>clr</c> (#1102 review). So a script may not name a dunder attribute or variable — except
/// <c>__init__</c> and <c>__name__</c> — nor a frame, traceback, code or function-internals
/// attribute, whether written as <c>x.attr</c> (checked in the syntax tree at compile, so at
/// deploy) or passed to <c>getattr</c> / <c>setattr</c> / <c>delattr</c> / <c>hasattr</c>
/// (checked at run time).
/// </summary>
internal static class PythonScriptPolicy
{
    private static readonly HashSet<string> AllowedDunders = new(StringComparer.Ordinal) { "__init__", "__name__" };

    private static readonly HashSet<string> BlockedAttributes = new(StringComparer.Ordinal)
    {
        "tb_frame", "tb_next", "tb_lasti", "tb_lineno",
        "f_back", "f_builtins", "f_globals", "f_locals", "f_code", "f_trace", "f_lasti", "f_lineno",
        "gi_frame", "gi_code", "gi_running", "gi_yieldfrom",
        "cr_frame", "cr_code", "cr_running", "cr_await",
        "ag_frame", "ag_code", "ag_running", "ag_await",
        "func_globals", "func_code", "func_closure", "func_defaults", "func_dict",
        "im_func", "im_self", "im_class",
        // IronPython: the .NET exception behind a Python one.
        "clsException",
    };

    // Wraps the attribute builtins so the name each receives passes Check first. Defined in Python
    // so the default argument of getattr and the error types stay Python's own; the wrappers'
    // internals (__closure__, __globals__) are dunders, so a script cannot reach the originals.
    private const string AttributeBuiltinsSource = """
        def make(_getattr, _setattr, _delattr, _hasattr, _check):
            def getattr(obj, name, *default):
                return _getattr(obj, _check(name), *default)
            def setattr(obj, name, value):
                return _setattr(obj, _check(name), value)
            def delattr(obj, name):
                return _delattr(obj, _check(name))
            def hasattr(obj, name):
                return _hasattr(obj, _check(name))
            return getattr, setattr, delattr, hasattr
        """;

    public static bool IsRestricted(string name) =>
        (name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal) && !AllowedDunders.Contains(name))
        || BlockedAttributes.Contains(name);

    /// <summary>Throws naming the first restricted name the script uses, with its line.</summary>
    public static void Validate(ScriptEngine engine, ScriptSource source)
    {
        var context = (PythonContext)HostingHelpers.GetLanguageContext(engine);
        var compilerContext = new CompilerContext(HostingHelpers.GetSourceUnit(source), context.GetCompilerOptions(), ErrorSink.Default);
        using var parser = Parser.CreateParser(compilerContext, (PythonOptions)context.Options);
        parser.ParseFile(makeModule: false).Walk(new RestrictedNameWalker());
    }

    /// <summary>getattr, setattr, delattr and hasattr that refuse restricted names, for one run's builtins.</summary>
    public static (object GetAttr, object SetAttr, object DelAttr, object HasAttr) CreateAttributeBuiltins(
        ScriptEngine engine, object make, IDictionary<object, object?> realBuiltins)
    {
        var wrappers = (PythonTuple)engine.Operations.Invoke(make,
            realBuiltins["getattr"], realBuiltins["setattr"], realBuiltins["delattr"], realBuiltins["hasattr"],
            new Func<object?, string>(CheckAttributeName));
        return (wrappers[0], wrappers[1], wrappers[2], wrappers[3]);
    }

    /// <summary>Compiles the wrapper factory in a scope with no builtins of its own.</summary>
    public static object CompileAttributeBuiltinsFactory(ScriptEngine engine)
    {
        var scope = engine.CreateScope();
        scope.SetVariable("__builtins__", new PythonDictionary());
        engine.Execute(AttributeBuiltinsSource, scope);
        return scope.GetVariable("make");
    }

    private static string CheckAttributeName(object? name)
    {
        // Only an exact str: a str subclass could override the methods the check relies on.
        if (name is not string text)
            throw PythonOps.TypeError("attribute name must be str, not {0}", name is null ? "NoneType" : IronPython.Runtime.Types.PythonType.Get__name__(IronPython.Runtime.Types.DynamicHelpers.GetPythonType(name)));
        if (IsRestricted(text))
            throw PythonOps.AttributeError("attribute '{0}' is not allowed in a script", text);
        return text;
    }

    private sealed class RestrictedNameWalker : PythonWalker
    {
        public override bool Walk(MemberExpression node)
        {
            Check(node.Name, node);
            return true;
        }

        public override bool Walk(NameExpression node)
        {
            Check(node.Name, node);
            return true;
        }

        public override bool Walk(FromImportStatement node)
        {
            foreach (var name in node.Names)
                Check(name, node);
            return true;
        }

        private static void Check(string name, Node node)
        {
            // The parser's own names (a generator expression's "__gen_$_parm__") hold a '$' that a
            // script cannot write.
            if (!name.Contains('$') && IsRestricted(name))
                throw new InvalidOperationException($"Python scripts may not use '{name}' (line {node.Start.Line}).");
        }
    }
}
