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

        // Assert — written integers come back as long; the untouched variable keeps its int
        Assert.AreEqual(42L, result["total"]);
        Assert.AreEqual("Hi Ada", result["greeting"]);
        Assert.AreEqual(21, result["amount"]);
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

        // Assert — results merge into the scope, so a deleted property is not removed
        Assert.IsTrue(result.ContainsKey("items"));
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
    public async Task Execute_UntouchedVariables_KeepTheirDotNetValues()
    {
        // Arrange — code review on #1101: values JSON cannot carry exactly must survive a JS task
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(5));
        dynamic variables = new ExpandoObject();
        variables.orderId = id;
        variables.amount = 19.99m;
        variables.externalId = 9007199254740993L;
        variables.at = at;
        variables.count = 3;
        variables.code = "2024-01-01T00:00:00";

        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute("_context.seen = true;", variables);

        // Assert
        Assert.AreEqual(id, result["orderId"]);
        Assert.AreEqual(19.99m, result["amount"]);
        Assert.AreEqual(9007199254740993L, result["externalId"]);
        Assert.AreEqual(at, result["at"]);
        Assert.AreEqual(3, result["count"]);
        Assert.AreEqual("2024-01-01T00:00:00", result["code"]);
        Assert.AreEqual(true, result["seen"]);
    }

    [TestMethod]
    public async Task Execute_WrittenDateLikeString_StaysAString()
    {
        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute(
            "_context.day = '1990-05-01'; _context.stamp = new Date(Date.UTC(2026, 0, 2)).toJSON();", new ExpandoObject());

        // Assert
        Assert.AreEqual("1990-05-01", result["day"]);
        Assert.AreEqual("2026-01-02T00:00:00.000Z", result["stamp"]);
    }

    [TestMethod]
    public async Task Execute_NonFiniteVariableInScope_DoesNotBreakTheScript()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.ratio = double.PositiveInfinity;

        // Act
        var result = (IDictionary<string, object?>)await _engine.Execute("_context.ok = 1;", variables);

        // Assert
        Assert.AreEqual(double.PositiveInfinity, result["ratio"]);
        Assert.AreEqual(1L, result["ok"]);
    }

    [TestMethod]
    [DataRow("_context = new Date();")]
    [DataRow("_context = new String('x');")]
    [DataRow("_context = [1, 2];")]
    [DataRow("_context = undefined;")]
    public async Task Execute_ContextNotAPlainObject_ThrowsTheContextError(string script)
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.Execute(script, new ExpandoObject()));
        StringAssert.Contains(ex.Message, "must leave _context an object");
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
