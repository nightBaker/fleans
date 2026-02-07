using System.Dynamic;
using Fleans.Infrastructure.Scripts;

namespace Fleans.Infrastructure.Tests;

[TestClass]
public class DynamicExpressoScriptExpressionExecutorTests
{
    private DynamicExpressoScriptExpressionExecutor _executor = null!;

    [TestInitialize]
    public void Setup()
    {
        _executor = new DynamicExpressoScriptExpressionExecutor();
    }

    [TestMethod]
    public async Task Execute_ShouldAssignNewVariable()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.x = 10;

        // Act
        var result = await _executor.Execute("_context.y = _context.x * 2", variables, "csharp");

        // Assert
        var dict = (IDictionary<string, object>)result;
        Assert.AreEqual(20, dict["y"]);
    }

    [TestMethod]
    public async Task Execute_ShouldHandleMultipleStatements()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.a = 5;

        // Act
        var result = await _executor.Execute("_context.b = _context.a + 1; _context.c = _context.b * 3", variables, "csharp");

        // Assert
        var dict = (IDictionary<string, object>)result;
        Assert.AreEqual(6, dict["b"]);
        Assert.AreEqual(18, dict["c"]);
    }

    [TestMethod]
    public async Task Execute_ShouldMutateExistingVariable()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.count = 1;

        // Act
        var result = await _executor.Execute("_context.count = _context.count + 10", variables, "csharp");

        // Assert
        var dict = (IDictionary<string, object>)result;
        Assert.AreEqual(11, dict["count"]);
    }

    [TestMethod]
    public async Task Execute_ShouldThrowForUnsupportedScriptFormat()
    {
        // Arrange
        dynamic variables = new ExpandoObject();

        // Act & Assert
        await Assert.ThrowsAsync<NotSupportedException>(
            () => _executor.Execute("_context.x = 1", variables, "javascript"));
    }

    [TestMethod]
    public async Task Execute_ShouldReturnVariablesUnchanged_WhenScriptIsEmpty()
    {
        // Arrange
        dynamic variables = new ExpandoObject();
        variables.x = 42;

        // Act
        var result = await _executor.Execute("", variables, "csharp");

        // Assert
        var dict = (IDictionary<string, object>)result;
        Assert.AreEqual(42, dict["x"]);
    }

    [TestMethod]
    public async Task Execute_ShouldHandleSemicolonsInsideStringLiterals()
    {
        // Arrange
        dynamic variables = new ExpandoObject();

        // Act
        var result = await _executor.Execute("_context.msg = \"Hello; World\"; _context.x = 1", variables, "csharp");

        // Assert
        var dict = (IDictionary<string, object>)result;
        Assert.AreEqual("Hello; World", dict["msg"]);
        Assert.AreEqual(1, dict["x"]);
    }

    [TestMethod]
    public void SplitStatements_ShouldSplitOnSemicolonsOutsideQuotes()
    {
        // Act
        var statements = DynamicExpressoScriptExpressionExecutor.SplitStatements(
            "_context.a = 1; _context.b = \"hi;there\"; _context.c = 3").ToList();

        // Assert
        Assert.AreEqual(3, statements.Count);
        Assert.AreEqual("_context.a = 1", statements[0]);
        Assert.AreEqual("_context.b = \"hi;there\"", statements[1]);
        Assert.AreEqual("_context.c = 3", statements[2]);
    }

    [TestMethod]
    public void SplitStatements_ShouldHandleTrailingSemicolon()
    {
        // Act
        var statements = DynamicExpressoScriptExpressionExecutor.SplitStatements(
            "_context.a = 1;").ToList();

        // Assert
        Assert.AreEqual(1, statements.Count);
        Assert.AreEqual("_context.a = 1", statements[0]);
    }
}
