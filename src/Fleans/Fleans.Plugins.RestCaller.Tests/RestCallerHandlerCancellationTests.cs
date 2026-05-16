using System.Dynamic;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleans.Plugins.RestCaller.Tests;

/// <summary>
/// Verifies the plugin-side cancellation contract added by #568: when the supplied
/// <see cref="CancellationToken"/> is signalled, <see cref="RestCallerHandler"/> propagates
/// <see cref="OperationCanceledException"/> (via the linked-CTS path in
/// <c>RestCallerHandler.cs:113-122</c>) rather than swallowing it or wrapping it in a
/// different exception type. The base class's <c>when (_grainLifetimeCts?.IsCancellationRequested == true)</c>
/// catch then routes the OCE to redelivery instead of FailActivity.
/// </summary>
[TestClass]
public class RestCallerHandlerCancellationTests
{
    private static RestCallerHandler MakeHandler(HttpMessageHandler messageHandler) =>
        new(new HttpClient(messageHandler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<RestCallerHandler>.Instance,
            Substitute.For<IGrainFactory>());

    private static CustomTaskExecutionContext Ctx() =>
        new(WorkflowInstanceId: Guid.NewGuid(),
            WorkflowId: "wf",
            ProcessDefinitionId: null,
            ActivityInstanceId: Guid.NewGuid(),
            ActivityId: "ct1",
            TaskType: "rest-call");

    private static IDictionary<string, object?> Inputs(string url) => new Dictionary<string, object?>
    {
        ["url"] = url,
        ["method"] = "GET",
        ["timeoutSec"] = 30,
    };

    [TestMethod]
    public async Task ExecuteAsync_PreCancelledToken_PropagatesOperationCanceledException()
    {
        // Stub HttpMessageHandler that blocks forever until cancelled — proves the plugin
        // honors the caller's token, not just its own internal timeout.
        var handler = MakeHandler(new BlockingHandler());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            handler.ExecuteForTest(
                Inputs("https://example.invalid/"),
                new ExpandoObject(),
                Ctx(),
                cts.Token));
    }

    [TestMethod]
    public async Task ExecuteAsync_TokenCancelledMidRequest_PropagatesOperationCanceledException()
    {
        var blocker = new BlockingHandler();
        var handler = MakeHandler(blocker);

        using var cts = new CancellationTokenSource();
        var task = handler.ExecuteForTest(
            Inputs("https://example.invalid/"),
            new ExpandoObject(),
            Ctx(),
            cts.Token);

        // Let the HTTP send park on the cancellation token, then trigger.
        await blocker.SendAsyncStarted;
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await task);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsyncStarted => _started.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            // Unreachable — Task.Delay throws on cancellation.
            return new HttpResponseMessage();
        }
    }
}
