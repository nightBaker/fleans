using System.Dynamic;
using System.Globalization;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.E2E.ExternalWorker;

/// <summary>
/// Test-only custom-task handler. Reports the worker it ran on as <c>__response.workerName</c>, so a
/// spec can assert from workflow variables alone that the task left the cluster.
///
/// Inputs (all optional): <c>delayMs</c> waits on the task's <see cref="CancellationToken"/> (the load
/// tests' work time, and what makes cancellation observable); <c>failCode</c> fails the task with
/// that BPMN error code; <c>callbackUrl</c> (the E2E TestHttpServer base URL) and <c>probeKey</c>
/// (correlation id, default the activity instance id) report <c>started</c>, <c>completed</c> or
/// <c>cancelled</c> to <c>{callbackUrl}/probe</c>, so the test process can see what happened inside
/// the handler even after the workflow has moved on.
/// </summary>
public sealed partial class ProbeHandler(IOptions<ExternalTaskWorkerOptions> options, ILogger<ProbeHandler> logger) : ExternalTaskHandler
{
    public const string ProbeTaskType = "e2e-probe";
    public const string ExternalProbeTaskType = "e2e-ext-probe";
    public const string PrivateTaskType = "e2e-ext-private";
    public const string VersionedTaskType = "e2e-ext-versioned";
    public const string VersionedTaskVersion = "1.2.0";

    private static readonly HttpClient CallbackClient = new();

    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        var workerName = options.Value.WorkerName;
        var callbackUrl = ReadString(resolvedInputs, "callbackUrl");
        var probeKey = ReadString(resolvedInputs, "probeKey") ?? context.ActivityInstanceId.ToString();

        await NotifyAsync(callbackUrl, probeKey, "started", workerName);

        if (int.TryParse(ReadString(resolvedInputs, "delayMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delayMs)
            && delayMs > 0)
        {
            try
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await NotifyAsync(callbackUrl, probeKey, "cancelled", workerName);
                throw;
            }
        }

        if (ReadString(resolvedInputs, "failCode") is { Length: > 0 } failCode)
            throw new ExternalTaskFailedException(failCode, "The probe was asked to fail.");

        await NotifyAsync(callbackUrl, probeKey, "completed", workerName);

        return new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?> { ["workerName"] = workerName },
        };
    }

    private async Task NotifyAsync(string? callbackUrl, string probeKey, string evt, string workerName)
    {
        if (string.IsNullOrEmpty(callbackUrl)) return;
        try
        {
            var url = $"{callbackUrl.TrimEnd('/')}/probe?key={Uri.EscapeDataString(probeKey)}" +
                      $"&event={evt}&silo={Uri.EscapeDataString(workerName)}";
            // Never tie the report to the (possibly cancelled) execution token.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await CallbackClient.GetAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            LogCallbackFailed(ex, evt);
        }
    }

    private static string? ReadString(IDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var raw) ? raw?.ToString() : null;

    [LoggerMessage(EventId = 99003, Level = LogLevel.Warning,
        Message = "e2e-probe callback '{Event}' could not be delivered")]
    private partial void LogCallbackFailed(Exception ex, string @event);
}
