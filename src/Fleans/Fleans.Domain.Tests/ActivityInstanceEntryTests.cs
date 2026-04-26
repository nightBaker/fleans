using Fleans.Domain.Errors;
using Fleans.Domain.States;

namespace Fleans.Domain.Tests;

[TestClass]
public class ActivityInstanceEntryTests
{
    private ActivityInstanceEntry CreateEntry(string activityId = "task1")
        => new(Guid.NewGuid(), activityId, Guid.NewGuid());

    [TestMethod]
    public void Execute_ShouldSetIsExecutingAndTimestamp()
    {
        var entry = CreateEntry();
        entry.Execute();
        Assert.IsTrue(entry.IsExecuting);
        Assert.IsNotNull(entry.ExecutionStartedAt);
    }

    [TestMethod]
    public void Execute_WhenAlreadyExecuting_ShouldThrow()
    {
        var entry = CreateEntry();
        entry.Execute();
        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Execute());
    }

    [TestMethod]
    public void Execute_WhenAlreadyCompleted_ShouldThrow()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Execute());
    }

    [TestMethod]
    public void Complete_ShouldSetIsCompletedAndClearIsExecuting()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        Assert.IsTrue(entry.IsCompleted);
        Assert.IsFalse(entry.IsExecuting);
        Assert.IsNotNull(entry.CompletedAt);
    }

    [TestMethod]
    public void Complete_WhenAlreadyCompleted_ShouldThrow()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Complete();
        Assert.ThrowsExactly<InvalidOperationException>(() => entry.Complete());
    }

    [TestMethod]
    public void Fail_WithGenericException_ShouldSetErrorCode500()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Fail(new Exception("something broke"));
        Assert.IsTrue(entry.IsCompleted);
        Assert.IsFalse(entry.IsExecuting);
        Assert.AreEqual("500", entry.ErrorCode);
        Assert.AreEqual("something broke", entry.ErrorMessage);
    }

    [TestMethod]
    public void Fail_WithActivityException_ShouldUseActivityErrorState()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Fail(new BadRequestActivityException("bad input"));
        Assert.AreEqual("400", entry.ErrorCode);
    }

    [TestMethod]
    public void Cancel_ShouldSetIsCancelledAndReason()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Cancel("gateway sibling completed");
        Assert.IsTrue(entry.IsCancelled);
        Assert.IsTrue(entry.IsCompleted);
        Assert.AreEqual("gateway sibling completed", entry.CancellationReason);
    }

    [TestMethod]
    public void ErrorState_ShouldReturnNullWhenNoError()
    {
        var entry = CreateEntry();
        Assert.IsNull(entry.ErrorState);
    }

    [TestMethod]
    public void ErrorState_ShouldReturnValueAfterFail()
    {
        var entry = CreateEntry();
        entry.Execute();
        entry.Fail(new Exception("err"));
        Assert.IsNotNull(entry.ErrorState);
        Assert.AreEqual(500, entry.ErrorState!.Code);
    }
}
