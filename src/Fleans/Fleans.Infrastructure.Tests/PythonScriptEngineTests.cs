using System.Dynamic;
using System.Numerics;
using Fleans.Infrastructure.Scripts;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class PythonScriptEngineTests
{
    // One engine for the class: creating an IronPython runtime costs a few hundred milliseconds.
    private static readonly PythonScriptEngine Engine = new();

    [TestMethod]
    public async Task Execute_ShouldReadAndWriteContext()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.amount = 21;
        variables.name = "Ada";

        // Act
        var result = (IDictionary<string, object?>)await Engine.Execute(
            "_context['total'] = _context['amount'] * 2\n_context['greeting'] = f\"Hi {_context['name']}\"", variables);

        // Assert — written integers come back as long; the untouched variable keeps its int
        Assert.AreEqual(42L, result["total"]);
        Assert.AreEqual("Hi Ada", result["greeting"]);
        Assert.AreEqual(21, result["amount"]);
    }

    [TestMethod]
    public async Task Execute_ShouldSupportFunctionsLoopsClassesAndNestedValues()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.items = new List<object> { 1, 2, 3 };

        // Act
        var result = (IDictionary<string, object?>)await Engine.Execute("""
            class Order:
                def __init__(self, total):
                    self.total = total

            def square(x):
                return x * x

            total = 0
            for item in _context['items']:
                total += square(item)
            _context['order'] = {'sum': Order(total).total, 'tags': ('a', 'b'), 'ratio': 0.5, 'none': None}
            del _context['items']
            """, variables);

        // Assert — results merge into the scope, so a deleted key is not removed; a tuple becomes a list
        Assert.IsTrue(result.ContainsKey("items"));
        var order = (IDictionary<string, object?>)result["order"]!;
        Assert.AreEqual(14L, order["sum"]);
        Assert.AreEqual(0.5, order["ratio"]);
        Assert.IsNull(order["none"]);
        CollectionAssert.AreEqual(new List<object?> { "a", "b" }, (List<object?>)order["tags"]!);
    }

    [TestMethod]
    public async Task Execute_ShouldImportAllowedModules()
    {
        // Act
        var result = (IDictionary<string, object?>)await Engine.Execute("""
            import math
            from re import sub
            _context['root'] = math.sqrt(16)
            _context['slug'] = sub('[^a-z]+', '-', 'order no 7')
            """, new ExpandoObject());

        // Assert
        Assert.AreEqual(4.0, result["root"]);
        Assert.AreEqual("order-no-", result["slug"]);
    }

    [TestMethod]
    public async Task Execute_IntegersBeyondLong_RoundTrip()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.big = 9007199254740993L;

        // Act — Python ints are unbounded
        var result = (IDictionary<string, object?>)await Engine.Execute(
            "_context['next'] = _context['big'] + 1\n_context['huge'] = 10 ** 30", variables);

        // Assert
        Assert.AreEqual(9007199254740994L, result["next"]);
        Assert.AreEqual(BigInteger.Pow(10, 30), result["huge"]);
    }

    [TestMethod]
    public async Task Execute_UntouchedVariables_KeepTheirDotNetValues()
    {
        // Arrange — values JSON cannot carry exactly must survive a Python task
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(5));
        var due = new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc);
        dynamic variables = new ExpandoObject();
        variables.orderId = id;
        variables.amount = 19.99m;
        variables.at = at;
        variables.due = due;
        variables.count = 3;
        variables.ratio = double.PositiveInfinity;

        // Act
        var result = (IDictionary<string, object?>)await Engine.Execute("_context['seen'] = True", variables);

        // Assert
        Assert.AreEqual(id, result["orderId"]);
        Assert.AreEqual(19.99m, result["amount"]);
        Assert.AreEqual(at, result["at"]);
        Assert.AreEqual(due, result["due"]);
        Assert.AreEqual(3, result["count"]);
        Assert.AreEqual(double.PositiveInfinity, result["ratio"]);
        Assert.AreEqual(true, result["seen"]);
    }

    [TestMethod]
    public async Task Execute_ShouldAllowReplacingContextDict()
    {
        // Act
        var result = (IDictionary<string, object?>)await Engine.Execute("_context = {'only': 'this'}", new ExpandoObject());

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("this", result["only"]);
    }

    [TestMethod]
    [DataRow("_context = 5")]
    [DataRow("_context = [1, 2]")]
    [DataRow("del _context")]
    public async Task Execute_ContextNotADict_Throws(string script)
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.Execute(script, new ExpandoObject()));
        StringAssert.Contains(ex.Message, "must leave _context a dict");
    }

    [TestMethod]
    [DataRow("_context['x'] = {1, 2}", "_context['x'] is a set")]
    [DataRow("_context['x'] = {1: 'a'}", "dict keys must be str")]
    [DataRow("_context['x'] = [b'abc']", "_context['x'][0] is a bytes")]
    public async Task Execute_ValueNotJson_ThrowsNamingThePath(string script, string expected)
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine.Execute(script, new ExpandoObject()));
        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public async Task Execute_RaisedError_SurfacesTypeMessageAndLine()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.name = "Ada";

        // Act
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Engine.Execute("""
            def check(name):
                if name == 'Ada':
                    raise ValueError('bad input ' + name)
            check(_context['name'])
            """, (ExpandoObject)variables));

        // Assert — a plain .NET exception: it crosses to the instance grain, and IronPython's own
        // exception types carry Python objects Orleans cannot serialize
        Assert.AreEqual("ValueError: bad input Ada (line 3)", ex.Message);
        Assert.IsNull(ex.InnerException);
        Assert.IsEmpty(ex.Data);
    }

    [TestMethod]
    public async Task Execute_Timeout_IsAPlainTimeoutException()
    {
        // Arrange
        var engine = new PythonScriptEngine(TimeSpan.FromMilliseconds(100));

        // Act
        var ex = await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => engine.Execute("while True:\n    try:\n        pass\n    except:\n        pass", new ExpandoObject()));

        // Assert
        StringAssert.Contains(ex.Message, "0.1 s timeout");
        Assert.IsEmpty(ex.Data);
    }

    [TestMethod]
    [DataRow("import clr")]
    [DataRow("import System")]
    [DataRow("import sys")]
    [DataRow("import os")]
    [DataRow("_context['x'] = open('/etc/passwd').read()")]
    [DataRow("exec('x = 1')")]
    [DataRow("_context['x'] = eval('1')")]
    [DataRow("_context['x'] = 'abc'.GetType()")]
    [DataRow("_context['x'] = __builtins__['__import__']('clr')")]
    public async Task Execute_HasNoDotNetOrHostAccess(string script)
    {
        // Act & Assert — no .NET bridge, no files, no dynamic code
        await Assert.ThrowsAsync<Exception>(() => Engine.Execute(script, new ExpandoObject()));
    }

    [TestMethod]
    public async Task Execute_DisallowedImport_NamesTheAllowedModules()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<Exception>(() => Engine.Execute("import sys", new ExpandoObject()));
        StringAssert.Contains(ex.Message, "import of 'sys' is not allowed");
        StringAssert.Contains(ex.Message, "math");
    }

    [TestMethod]
    public async Task Execute_RunsDoNotShareBuiltinsOrModules()
    {
        // Arrange
        await Engine.Execute("""
            import math
            math.pi = 3
            __builtins__['len'] = None
            leaked = 1
            """, new ExpandoObject());

        // Act
        var result = (IDictionary<string, object?>)await Engine.Execute("""
            import math
            _context['pi'] = math.pi
            _context['len'] = len([1, 2])
            _context['leaked'] = 'leaked' in dir()
            """, new ExpandoObject());

        // Assert
        Assert.AreEqual(Math.PI, result["pi"]);
        Assert.AreEqual(2L, result["len"]);
        Assert.AreEqual(false, result["leaked"]);
    }

    [TestMethod]
    [DataRow("while True:\n    pass")]
    [DataRow("while True:\n    try:\n        pass\n    except:\n        pass")]
    [DataRow("while True:\n    try:\n        while True:\n            pass\n    except BaseException:\n        continue")]
    public async Task Execute_InfiniteLoop_IsStoppedByTimeout(string script)
    {
        // Arrange
        var engine = new PythonScriptEngine(TimeSpan.FromMilliseconds(200));

        // Act & Assert — the limit raises on every line past the deadline, so except: cannot swallow it
        await Assert.ThrowsAsync<TimeoutException>(() => engine.Execute(script, new ExpandoObject()))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [TestMethod]
    public async Task Execute_UnboundedRecursion_RaisesRecursionError()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<Exception>(
            () => Engine.Execute("def f(n):\n    return f(n + 1)\nf(0)", new ExpandoObject()));
        StringAssert.Contains(ex.Message, "recursion");
    }

    [TestMethod]
    public async Task Execute_RecursionThatSwallowsErrors_DoesNotOverflowTheStack()
    {
        // Arrange — a function that throws often is recompiled by IronPython without its recursion
        // check; without the stack budget this script takes the whole process down
        const string script = """
            def f():
                while True:
                    try:
                        f()
                    except:
                        pass
            f()
            """;
        var engine = new PythonScriptEngine(TimeSpan.FromSeconds(2));

        // Act & Assert — the run ends with one of the limits; reaching this line means the process survived
        var ex = await Assert.ThrowsAsync<Exception>(() => engine.Execute(script, new ExpandoObject()))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(ex is TimeoutException or InsufficientExecutionStackException, ex.GetType().Name);
    }

    [TestMethod]
    public async Task Execute_ConcurrentRuns_DoNotInterfere()
    {
        // Act
        var runs = Enumerable.Range(0, 20).Select(async i =>
        {
            dynamic variables = new ExpandoObject();
            variables.n = i;
            var result = (IDictionary<string, object?>)await Engine.Execute(
                "_context['square'] = sum(_context['n'] for _ in range(_context['n']))", variables);
            return (i, square: (long)result["square"]!);
        });

        // Assert
        foreach (var (i, square) in await Task.WhenAll(runs))
            Assert.AreEqual((long)i * i, square);
    }

    [TestMethod]
    public async Task Execute_SameScriptTwice_CompilesOnce()
    {
        // Arrange
        var engine = new PythonScriptEngine();
        const string script = "_context['y'] = _context['x'] + 1";
        dynamic first = new ExpandoObject();
        first.x = 1;
        dynamic second = new ExpandoObject();
        second.x = 10;

        // Act
        await engine.Execute(script, first);
        var result = (IDictionary<string, object?>)await engine.Execute(script, second);

        // Assert
        Assert.AreEqual(1, engine.CachedScriptCount);
        Assert.AreEqual(11L, result["y"]);
    }

    [TestMethod]
    public async Task Execute_EmptyScript_ReturnsVariablesUnchanged()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.x = 1;

        // Act
        var result = await Engine.Execute("  ", variables);

        // Assert
        Assert.AreSame(variables, result);
    }

    [TestMethod]
    public void Compile_SyntaxError_ThrowsAndIsNotCached()
    {
        // Arrange
        var engine = new PythonScriptEngine();

        // Act & Assert
        Assert.Throws<Exception>(() => engine.Compile("_context['y'] = "));
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => engine.Compile("x = 1\nif True:\ny = 1"));
        StringAssert.Contains(ex.Message, "line 3");
        Assert.AreEqual(0, engine.CachedScriptCount);
        engine.Compile("_context['y'] = 1");
        Assert.AreEqual(1, engine.CachedScriptCount);
    }
}
