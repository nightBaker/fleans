using System.Dynamic;
using Fleans.Application.CustomTasks;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class CustomTaskCallProviderRegistryTests
{
    public interface IRestCallerGrain : ICustomTaskCallProvider { }
    public interface IOtherGrain : ICustomTaskCallProvider { }

    [TestMethod]
    public void Resolves_RegisteredTaskType_ToGrainInterface()
    {
        var registry = new CustomTaskCallProviderRegistry(
        [
            new CustomTaskRegistration("rest-call", typeof(IRestCallerGrain))
        ]);

        Assert.IsTrue(registry.TryGetGrainInterface("rest-call", out var t));
        Assert.AreEqual(typeof(IRestCallerGrain), t);
    }

    [TestMethod]
    public void Resolves_TaskType_CaseInsensitively()
    {
        var registry = new CustomTaskCallProviderRegistry(
        [
            new CustomTaskRegistration("rest-call", typeof(IRestCallerGrain))
        ]);

        Assert.IsTrue(registry.TryGetGrainInterface("REST-CALL", out _));
        Assert.IsTrue(registry.TryGetGrainInterface("Rest-Call", out _));
    }

    [TestMethod]
    public void Constructor_DuplicateTaskType_ThrowsInvalidOperationException()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            _ = new CustomTaskCallProviderRegistry(
            [
                new CustomTaskRegistration("rest-call", typeof(IRestCallerGrain)),
                new CustomTaskRegistration("rest-call", typeof(IOtherGrain)),
            ]));

        StringAssert.Contains(ex.Message, "Duplicate");
        StringAssert.Contains(ex.Message, "rest-call");
    }

    [TestMethod]
    public void Returns_False_ForUnknownTaskType()
    {
        var registry = new CustomTaskCallProviderRegistry([]);
        Assert.IsFalse(registry.TryGetGrainInterface("not-registered", out var t));
        Assert.IsNull(t);
    }
}
