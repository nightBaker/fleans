using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class ScriptTaskTests
{
    [TestMethod]
    public void ScriptTask_ShouldHaveCorrectProperties()
    {
        // Arrange & Act
        var task = new ScriptTask("script1", "_context.x = 10", "csharp");

        // Assert
        Assert.AreEqual("script1", task.ActivityId);
        Assert.AreEqual("_context.x = 10", task.Script);
        Assert.AreEqual("csharp", task.ScriptFormat);
    }

    [TestMethod]
    public void ScriptTask_ShouldThrowOnNullScript()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScriptTask("script1", null!));
    }

    [TestMethod]
    public void ScriptTask_ShouldDefaultScriptFormatToCsharp()
    {
        // Arrange & Act
        var task = new ScriptTask("script1", "_context.x = 10");

        // Assert
        Assert.AreEqual("csharp", task.ScriptFormat);
    }
}
