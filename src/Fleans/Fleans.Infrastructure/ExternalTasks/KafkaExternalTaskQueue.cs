using System.Text.RegularExpressions;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using ErrorCode = Confluent.Kafka.ErrorCode;
using Fleans.Application.ExternalTasks;
using Fleans.Domain.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Fleans.Infrastructure.ExternalTasks;

/// <summary>
/// Publishes external tasks to Kafka (#979): topic <c>{TopicPrefix}{taskType}</c>, key = workflow
/// instance id, value = the JSON task record (<see cref="Serialize"/>). Workers read with their own
/// consumer group and complete over REST. The producer is idempotent with <c>acks=all</c>, and
/// <see cref="Publish"/> returns once the broker acknowledged the record — or throws within
/// <c>message.timeout.ms</c> (10 s unless set in <c>Config</c>), and the instance fails the task. On startup it checks the
/// topic name of every configured task type and creates missing topics (<see cref="KafkaExternalTaskOptions.CreateTopics"/>).
/// </summary>
internal sealed partial class KafkaExternalTaskQueue : IExternalTaskQueue, IHostedService, IDisposable
{
    private static readonly Regex LegalTopic = new("^[a-zA-Z0-9._-]{1,249}$", RegexOptions.Compiled);

    private static readonly JsonSerializer Json = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    });

    private readonly KafkaExternalTaskOptions _options;
    private readonly ExternalTaskOptions _externalTasks;
    private readonly ILogger<KafkaExternalTaskQueue> _logger;
    private readonly IProducer<string, string> _producer;

    public KafkaExternalTaskQueue(
        IOptions<KafkaExternalTaskOptions> options,
        IOptions<ExternalTaskOptions> externalTasks,
        ILogger<KafkaExternalTaskQueue> logger)
    {
        _options = options.Value;
        _externalTasks = externalTasks.Value;
        _logger = logger;

        var config = new ProducerConfig(new Dictionary<string, string>(_options.Config))
        {
            BootstrapServers = _options.Brokers,
            Acks = Acks.All,
            EnableIdempotence = true,
        };
        // Publish runs inside the workflow instance's turn: give up well before the 30 s grain-call
        // timeout (librdkafka's default is 5 minutes) so a broker outage fails the task promptly.
        if (!_options.Config.ContainsKey("message.timeout.ms"))
            config.MessageTimeoutMs = 10_000;
        _producer = new ProducerBuilder<string, string>(config)
            .SetErrorHandler((_, e) => LogProducerError(e.Code, e.Reason))
            .Build();
    }

    public static string TopicFor(KafkaExternalTaskOptions options, string taskType) => options.TopicPrefix + taskType;

    /// <summary>
    /// The task record workers receive. Variables are the task's input mappings; the activity
    /// instance id is the idempotency key and goes back with the completion.
    /// </summary>
    public static string Serialize(ExecuteCustomTaskEvent task)
    {
        var record = new JObject
        {
            ["workflowInstanceId"] = task.WorkflowInstanceId,
            ["workflowId"] = task.WorkflowId,
            ["processDefinitionId"] = task.ProcessDefinitionId,
            ["activityInstanceId"] = task.ActivityInstanceId,
            ["activityId"] = task.ActivityId,
            ["taskType"] = task.TaskType,
            ["businessId"] = task.BusinessId,
            ["variables"] = JObject.FromObject(task.Inputs, Json),
        };
        return record.ToString(Formatting.None);
    }

    public async Task Publish(ExecuteCustomTaskEvent task, CancellationToken cancellationToken)
    {
        await _producer.ProduceAsync(
            TopicFor(_options, task.TaskType),
            new Message<string, string> { Key = task.WorkflowInstanceId.ToString("D"), Value = Serialize(task) },
            cancellationToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Brokers))
            throw new InvalidOperationException("Fleans:ExternalTasks:Kafka:Brokers is required for the kafka external-task provider.");

        var topics = _externalTasks.TaskTypes.Select(t => TopicFor(_options, t)).Distinct().ToList();
        var illegal = topics.Where(t => !LegalTopic.IsMatch(t)).ToList();
        if (illegal.Count > 0)
            throw new InvalidOperationException(
                $"External-task topic names must match [a-zA-Z0-9._-]{{1,249}}: {string.Join(", ", illegal)}. " +
                "Rename the task types or change Fleans:ExternalTasks:Kafka:TopicPrefix.");

        if (!_options.CreateTopics || topics.Count == 0)
            return;

        var adminConfig = new AdminClientConfig(new Dictionary<string, string>(_options.Config))
        {
            BootstrapServers = _options.Brokers,
        };
        using var admin = new AdminClientBuilder(adminConfig).Build();
        try
        {
            await admin.CreateTopicsAsync(topics.Select(t => new TopicSpecification
            {
                Name = t,
                NumPartitions = _options.NumPartitions,
                ReplicationFactor = _options.ReplicationFactor,
            }));
            LogTopicsCreated(string.Join(", ", topics));
        }
        catch (CreateTopicsException ex) when (ex.Results.All(r =>
            r.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists))
        {
            var created = ex.Results.Where(r => r.Error.Code == ErrorCode.NoError).Select(r => r.Topic).ToList();
            if (created.Count > 0)
                LogTopicsCreated(string.Join(", ", created));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _producer.Flush(cancellationToken);
        return Task.CompletedTask;
    }

    public void Dispose() => _producer.Dispose();

    [LoggerMessage(EventId = 14510, Level = LogLevel.Information,
        Message = "External-task topics created: {Topics}")]
    private partial void LogTopicsCreated(string topics);

    [LoggerMessage(EventId = 14511, Level = LogLevel.Warning,
        Message = "External-task Kafka producer error {Code}: {Reason}")]
    private partial void LogProducerError(ErrorCode code, string reason);
}
