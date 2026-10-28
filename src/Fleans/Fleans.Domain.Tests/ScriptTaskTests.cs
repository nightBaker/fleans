using Fleans.Domain.Activities;

namespace Fleans.Domain.Tests;

[TestClass]
public class ScriptTaskTests
{

    [TestMethod]
    public void ScriptTask_ShouldThrowOnNullScript()
    {
        // Act & Assert
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScriptTask("script1", null!));
    }
}
