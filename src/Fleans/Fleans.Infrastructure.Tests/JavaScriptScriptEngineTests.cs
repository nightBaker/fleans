using System.Dynamic;
using Fleans.Infrastructure.Scripts;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class JavaScriptScriptEngineTests
{
    private JavaScriptScriptEngine _engine = null!;

    [TestInitialize]
    public void Setup()
    {
        _engine = new JavaScriptScriptEngine();
    }

    [TestMethod]
    public async Task Execute_ShouldReadAndWriteContext()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.amount = 21;
        variables.name = "Ada";

        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute(
            "_context.total = _context.amount * 2; _context.greeting = `Hi ${_context.name}`;", variables);

        // Assert — untouched variables are kept, JSON integers come back as long
        Assert.AreEqual(42L, result["total"]);
        Assert.AreEqual("Hi Ada", result["greeting"]);
        Assert.AreEqual(21L, result["amount"]);
    }

    [TestMethod]
    public async Task Execute_ShouldSupportFunctionsLoopsAndNestedValues()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.items = new List<object> { 1, 2, 3 };

        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute("""
            function square(x) { return x * x; }
            let sum = 0;
            for (const item of _context.items) { sum += square(item); }
            _context.order = { sum: sum, tags: ['a', 'b'], ratio: 0.5 };
            delete _context.items;
            """, variables);

        // Assert
        Assert.IsFalse(result.ContainsKey("items"), "A deleted property is removed");
        var order = (IDictionary<string, object?>)result["order"]!;
        Assert.AreEqual(14L, order["sum"]);
        Assert.AreEqual(0.5, order["ratio"]);
        CollectionAssert.AreEqual(new List<object?> { "a", "b" }, (List<object?>)order["tags"]!);
    }

    [TestMethod]
    public async Task Execute_ShouldKeepDateTimeVariables()
    {
        // Arrange
        var due = new DateTime(2026, 11, 20, 10, 30, 0, DateTimeKind.Utc);
        dynamic variables = new ExpandoObject();
        variables.due = due;

        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute("_context.seen = true;", variables);

        // Assert
        Assert.AreEqual(due, result["due"]);
        Assert.AreEqual(true, result["seen"]);
    }

    [TestMethod]
    public async Task Execute_ShouldAllowReplacingContextObject()
    {
        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute("_context = { only: 'this' };", new ExpandoObject());

        // Assert
        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("this", result["only"]);
    }

    [TestMethod]
    public async Task Execute_ContextNotAnObject_Throws()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.Execute("_context = 5;", new ExpandoObject()));
    }

    [TestMethod]
    public async Task Execute_ThrownError_SurfacesItsMessage()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<Jint.Runtime.JavaScriptException>(
            () => _engine.Execute("throw new Error('bad input');", new ExpandoObject()));
        StringAssert.Contains(ex.Message, "bad input");
    }

    [TestMethod]
    public async Task Execute_InfiniteLoop_IsStoppedByTimeout()
    {
        // Arrange
        var engine = new JavaScriptScriptEngine(TimeSpan.FromMilliseconds(100));

        // Act & Assert — Jint ends the run itself, so the thread is not left spinning
        await Assert.ThrowsAsync<TimeoutException>(() => engine.Execute("while (true) {}", new ExpandoObject()));
    }

    [TestMethod]
    public async Task Execute_HasNoDotNetAccess()
    {
        // Act & Assert — no CLR interop is enabled, so .NET types are not reachable
        await Assert.ThrowsAsync<Jint.Runtime.JavaScriptException>(
            () => _engine.Execute("_context.x = System.IO.File.ReadAllText('/etc/passwd');", new ExpandoObject()));
    }

    [TestMethod]
    public async Task Execute_RunsDoNotShareGlobals()
    {
        // Arrange
        await _engine.Execute("globalThis.leaked = 1;", new ExpandoObject());

        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute(
            "_context.leaked = typeof leaked;", new ExpandoObject());

        // Assert
        Assert.AreEqual("undefined", result["leaked"]);
    }

    [TestMethod]
    public async Task Execute_SameScriptTwice_ParsesOnce()
    {
        // Arrange
        const string script = "_context.y = _context.x + 1;";
        dynamic first = new ExpandoObject();
        first.x = 1;
        dynamic second = new ExpandoObject();
        second.x = 10;

        // Act
        await _engine.Execute(script, first);
        var result = (IDictionary<string, object?>)await _engine.Execute(script, second);

        // Assert
        Assert.AreEqual(1, _engine.CachedScriptCount);
        Assert.AreEqual(11L, result["y"]);
    }

    [TestMethod]
    public async Task Execute_EmptyScript_ReturnsVariablesUnchanged()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.x = 1;

        // Act
        var result = await _engine.Execute("  ", variables);

        // Assert
        Assert.AreSame(variables, result);
    }

    [TestMethod]
    public void Compile_ValidScript_IsCached()
    {
        // Act
        _engine.Compile("_context.y = 1;");

        // Assert
        Assert.AreEqual(1, _engine.CachedScriptCount);
    }

    [TestMethod]
    public void Compile_SyntaxError_ThrowsAndIsNotCached()
    {
        // Act & Assert
        Assert.Throws<Exception>(() => _engine.Compile("_context.y = ;"));
        Assert.AreEqual(0, _engine.CachedScriptCount);
    }
}
