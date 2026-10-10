using System.Dynamic;
using System.Globalization;
using Fleans.Application.CustomTasks;
using Fleans.Worker.CustomTasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Fleans.E2E.PluginHost;

/// <summary>
/// Test-only custom-task plugin backing <c>&lt;serviceTask type="e2e-probe"&gt;</c>.
/// Reports where it ran (silo name, <c>Fleans:Role</c>, process id) as
/// <c>__response.*</c> so a spec can assert placement from workflow variables alone, and —
/// when given <c>delayMs</c> — blocks on the grain-lifetime <see cref="CancellationToken"/>
/// so cancellation behaviour is observable.
///
/// Inputs (all optional): <c>delayMs</c> (int), <c>callbackUrl</c> (the E2E TestHttpServer
/// base URL) and <c>probeKey</c> (correlation id). With a callback URL the probe reports
/// <c>started</c>, <c>completed</c> or <c>cancelled</c> to <c>{callbackUrl}/probe</c>, so the
/// test process can see what happened inside the plugin even after the workflow has moved on.
/// </summary>
[ImplicitStreamSubscription("events.ExecuteCustomTaskEvent.e2e-probe")]
public sealed partial class ProbeHandler : CustomTaskHandlerBase
{
    public const string ProbeTaskType = "e2e-probe";
    public const string CallbackClientName = "e2e-probe-callback";

    private readonly ILogger<ProbeHandler> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly string _siloName;

    public ProbeHandler(
        ILogger<ProbeHandler> logger,
        IGrainFactory grainFactory,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILocalSiloDetails siloDetails)
        : base(logger, grainFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _siloName = siloDetails.Name;
    }

    protected override string TaskType => ProbeTaskType;

    protected override async Task<IDictionary<string, object?>> ExecuteAsync(
        IDictionary<string, object?> resolvedInputs,
        ExpandoObject variables,
        CustomTaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        var delayMs = ReadInt(resolvedInputs, "delayMs");
        var callbackUrl = ReadString(resolvedInputs, "callbackUrl");
        var probeKey = ReadString(resolvedInputs, "probeKey") ?? context.ActivityInstanceId.ToString();

        LogProbeStarted(context.ActivityId, _siloName, delayMs);
        await NotifyAsync(callbackUrl, probeKey, "started");

        if (delayMs > 0)
        {
            try
            {
                await Task.Delay(delayMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                LogProbeCancelled(context.ActivityId, _siloName);
                await NotifyAsync(callbackUrl, probeKey, "cancelled");
                throw;
            }
        }

        LogProbeCompleted(context.ActivityId, _siloName);
        await NotifyAsync(callbackUrl, probeKey, "completed");

        return new Dictionary<string, object?>
        {
            ["__response"] = new Dictionary<string, object?>
            {
                ["siloName"] = _siloName,
                ["role"] = _configuration["Fleans:Role"],
                ["processId"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    private async Task NotifyAsync(string? callbackUrl, string probeKey, string evt)
    {
        if (string.IsNullOrEmpty(callbackUrl)) return;
        try
        {
            var url = $"{callbackUrl.TrimEnd('/')}/probe?key={Uri.EscapeDataString(probeKey)}" +
                      $"&event={evt}&silo={Uri.EscapeDataString(_siloName)}";
            using var client = _httpClientFactory.CreateClient(CallbackClientName);
            // Never tie the report to the (possibly cancelled) execution token.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await client.GetAsync(url, cts.Token);
        }
        catch (Exception ex)
        {
            LogProbeCallbackFailed(ex, evt);
        }
    }

    private static string? ReadString(IDictionary<string, object?> inputs, string key) =>
        inputs.TryGetValue(key, out var raw) ? raw?.ToString() : null;

    private static int ReadInt(IDictionary<string, object?> inputs, string key) =>
        int.TryParse(ReadString(inputs, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    [LoggerMessage(EventId = 99000, Level = LogLevel.Information,
        Message = "e2e-probe started for activity {ActivityId} on silo {SiloName} (delayMs={DelayMs})")]
    private partial void LogProbeStarted(string activityId, string siloName, int delayMs);

    [LoggerMessage(EventId = 99001, Level = LogLevel.Information,
        Message = "e2e-probe completed for activity {ActivityId} on silo {SiloName}")]
    private partial void LogProbeCompleted(string activityId, string siloName);

    [LoggerMessage(EventId = 99002, Level = LogLevel.Warning,
        Message = "e2e-probe observed cancellation for activity {ActivityId} on silo {SiloName}")]
    private partial void LogProbeCancelled(string activityId, string siloName);

    [LoggerMessage(EventId = 99003, Level = LogLevel.Warning,
        Message = "e2e-probe callback '{Event}' could not be delivered")]
    private partial void LogProbeCallbackFailed(Exception ex, string @event);
}

public static class ProbePluginServiceCollectionExtensions
{
    public static IServiceCollection AddProbePlugin(this IServiceCollection services) =>
        services.AddCustomTaskPlugin<ProbeHandler>(
            taskType: ProbeHandler.ProbeTaskType,
            displayName: "E2E Probe",
            parameterSchema: CustomTaskParameterSchema.Empty);
}
