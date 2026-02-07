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
        var result = await _executor.Execute("_context.y = _context.x * 2", variables);

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
        var result = await _executor.Execute("_context.b = _context.a + 1; _context.c = _context.b * 3", variables);

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
        var result = await _executor.Execute("_context.count = _context.count + 10", variables);

        // Assert
        var dict = (IDictionary<string, object>)result;
        Assert.AreEqual(11, dict["count"]);
    }
}
