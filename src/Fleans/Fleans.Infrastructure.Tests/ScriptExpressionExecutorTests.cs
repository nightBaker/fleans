using System.Dynamic;
using Fleans.Application.Scripts;
using Fleans.Infrastructure.Scripts;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class ScriptExpressionExecutorTests
{
    private readonly ScriptExpressionExecutor _executor =
        new([new DynamicExpressoScriptEngine(), new JavaScriptScriptEngine(), new PythonScriptEngine()]);

    [TestMethod]
    [DataRow("csharp", "_context.y = _context.x + 1")]
    [DataRow("C#", "_context.y = _context.x + 1")]
    [DataRow("", "_context.y = _context.x + 1")]
    [DataRow("javascript", "_context.y = _context.x + 1;")]
    [DataRow("JavaScript", "_context.y = _context.x + 1;")]
    [DataRow("js", "_context.y = _context.x + 1;")]
    [DataRow("python", "_context['y'] = _context['x'] + 1")]
    [DataRow("Python", "_context['y'] = _context['x'] + 1")]
    [DataRow("py", "_context['y'] = _context['x'] + 1")]
    public async Task Execute_RoutesByFormat_CaseInsensitive(string format, string script)
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.x = 1;

        // Act
        var result = (IDictionary<string, object?>)await _executor.Execute(script, variables, format);

        // Assert
        Assert.AreEqual(2L, Convert.ToInt64(result["y"]));
    }

    [TestMethod]
    public async Task Execute_NullFormat_RunsAsCSharp()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.x = 1;

        // Act
        var result = (IDictionary<string, object?>)await _executor.Execute("_context.y = _context.x + 1", variables, null!);

        // Assert
        Assert.AreEqual(2, result["y"]);
    }

    [TestMethod]
    public async Task Execute_UnsupportedFormat_ThrowsListingRegisteredFormats()
    {
        // Act & Assert
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => _executor.Execute("x = 1", new ExpandoObject(), "groovy"));
        StringAssert.Contains(ex.Message, "'groovy'");
        StringAssert.Contains(ex.Message, "csharp");
        StringAssert.Contains(ex.Message, "javascript");
        StringAssert.Contains(ex.Message, "python");
    }

    [TestMethod]
    public void Compile_UnsupportedFormat_Throws()
    {
        // Act & Assert
        Assert.ThrowsExactly<NotSupportedException>(() => _executor.Compile("x = 1", "ruby"));
    }

    [TestMethod]
    public void Compile_RoutesToTheFormatsEngine()
    {
        // Act & Assert — valid JavaScript that DynamicExpresso cannot parse
        _executor.Compile("const a = [1, 2].map(x => x * 2); _context.a = a;", "javascript");
        Assert.Throws<Exception>(() => _executor.Compile("const a = 1;", "csharp"));
        _executor.Compile("a = [x * 2 for x in [1, 2]]\n_context['a'] = a", "python");
        Assert.Throws<Exception>(() => _executor.Compile("const a = [1, 2].map(x => x * 2);", "python"));
    }

    [TestMethod]
    public void Constructor_TwoEnginesClaimingOneFormat_Throws()
    {
        // Act & Assert
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => new ScriptExpressionExecutor([new JavaScriptScriptEngine(), new JavaScriptScriptEngine()]));
        StringAssert.Contains(ex.Message, "javascript");
    }
}
