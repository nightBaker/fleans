using System.Dynamic;
using System.Globalization;
using Fleans.ExternalTasks.Sdk;
using Microsoft.Extensions.Options;

namespace Fleans.E2E.ExternalWorker;

/// <summary>
/// Test-only external-task handler. Waits the optional <c>delayMs</c> input (the load tests' work
/// time) and reports the worker it ran on as <c>__response.workerName</c>, so a spec can assert
/// from workflow variables alone that the task left the cluster.
/// </summary>
public sealed class ProbeHandler(IOptions<ExternalTaskWorkerOptions> options) : ExternalTaskHandler
{
    /// <summary>The task type the E2E stack routes to this worker (Fleans:ExternalTasks:TaskTypes).</summary>
    public const string DefaultTaskType = "e2e-ext-probe";
    public const string PrivateTaskType = "e2e-ext-private";

    public override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        ExternalTaskContext context,
        CancellationToken cancellationToken)
    {
        if (resolvedInputs.TryGetValue("delayMs", out var raw)
            && int.TryParse(raw?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delayMs)
            && delayMs > 0)
        {
            await Task.Delay(delayMs, cancellationToken);
        }

        return new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?> { ["workerName"] = options.Value.WorkerName },
        };
    }
}
