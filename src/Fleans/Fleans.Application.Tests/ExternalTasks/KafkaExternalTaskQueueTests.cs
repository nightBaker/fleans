using System.Dynamic;
using Confluent.Kafka;
using Fleans.Application.ExternalTasks;
using Fleans.Domain.Events;
using Fleans.Infrastructure.ExternalTasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Testcontainers.Kafka;

namespace Fleans.Application.Tests.ExternalTasks;

/// <summary>
/// The Kafka external-task queue (#979): the record workers read, the provider switch, and — with
/// <c>FLEANS_KAFKA_TESTS=1</c> and Docker — a publish read back from the task type's topic.
/// </summary>
[TestClass]
public class KafkaExternalTaskQueueTests
{
    private static bool ShouldRunKafka =>
        string.Equals(Environment.GetEnvironmentVariable("FLEANS_KAFKA_TESTS"), "1", StringComparison.Ordinal);

    [TestMethod]
    public void Record_carries_the_task_identity_and_its_inputs_in_camel_case()
    {
        // Arrange
        var task = Task("charge-card", ("amount", 20L), ("currency", "EUR"));

        // Act
        var record = JObject.Parse(KafkaExternalTaskQueue.Serialize(task));

        // Assert
        Assert.AreEqual(task.WorkflowInstanceId, record["workflowInstanceId"]!.ToObject<Guid>());
        Assert.AreEqual(task.ActivityInstanceId, record["activityInstanceId"]!.ToObject<Guid>());
        Assert.AreEqual("charge", (string?)record["activityId"]);
        Assert.AreEqual("charge-card", (string?)record["taskType"]);
        Assert.AreEqual("order-process", (string?)record["workflowId"]);
        Assert.AreEqual("ORDER-1", (string?)record["businessId"]);
        Assert.AreEqual(20L, (long)record["variables"]!["amount"]!);
        Assert.AreEqual("EUR", (string?)record["variables"]!["currency"]);
        Assert.IsNull(record["variablesId"], "the engine's scope id is not part of the record");
    }

    [TestMethod]
    public void No_provider_registers_no_queue()
    {
        var services = new ServiceCollection().AddExternalTasks(Config());

        Assert.IsFalse(services.Any(d => d.ServiceType == typeof(IExternalTaskQueue)));
    }

    [TestMethod]
    public void Kafka_provider_registers_the_kafka_queue()
    {
        var config = Config(("Fleans:ExternalTasks:Provider", "Kafka"), ("Fleans:ExternalTasks:Kafka:Brokers", "localhost:9092"));
        var services = new ServiceCollection().AddLogging().AddSingleton(config).AddExternalTasks(config);

        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<KafkaExternalTaskQueue>(provider.GetRequiredService<IExternalTaskQueue>());
    }

    [TestMethod]
    public void Unknown_provider_fails_at_startup()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            new ServiceCollection().AddExternalTasks(Config(("Fleans:ExternalTasks:Provider", "rabbit"))));

        Assert.Contains("rabbit", ex.Message);
    }

    [TestMethod]
    public async Task Illegal_topic_name_fails_at_startup()
    {
        using var queue = Queue("localhost:9092", "has space");

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queue.StartAsync(CancellationToken.None));

        Assert.Contains("fleans.tasks.has space", ex.Message);
    }

    [TestMethod]
    public async Task Published_task_is_read_back_from_its_task_type_topic_keyed_by_instance()
    {
        if (!ShouldRunKafka) { Assert.Inconclusive("FLEANS_KAFKA_TESTS != 1 — skipping container-backed test."); return; }

        // Arrange
        await using var kafka = new KafkaBuilder().WithImage("confluentinc/cp-kafka:7.6.1").Build();
        await kafka.StartAsync();
        using var queue = Queue(kafka.GetBootstrapAddress(), "charge-card");
        await queue.StartAsync(CancellationToken.None);
        var task = Task("charge-card", ("amount", 20L));

        // Act
        await queue.Publish(task, CancellationToken.None);

        // Assert
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = kafka.GetBootstrapAddress(),
            GroupId = "test-worker",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe("fleans.tasks.charge-card");
        var result = consumer.Consume(TimeSpan.FromSeconds(30));
        Assert.IsNotNull(result, "no record on fleans.tasks.charge-card");
        Assert.AreEqual(task.WorkflowInstanceId.ToString("D"), result.Message.Key);
        Assert.AreEqual(task.ActivityInstanceId, JObject.Parse(result.Message.Value)["activityInstanceId"]!.ToObject<Guid>());
    }

    private static KafkaExternalTaskQueue Queue(string brokers, string taskType) => new(
        Options.Create(new KafkaExternalTaskOptions { Brokers = brokers, ReplicationFactor = 1, NumPartitions = 2 }),
        Options.Create(new ExternalTaskOptions { Provider = "kafka", TaskTypes = [taskType] }),
        NullLogger<KafkaExternalTaskQueue>.Instance);

    private static ExecuteCustomTaskEvent Task(string taskType, params (string Key, object Value)[] inputs)
    {
        var vars = new ExpandoObject();
        foreach (var (key, value) in inputs)
            ((IDictionary<string, object?>)vars)[key] = value;
        return new ExecuteCustomTaskEvent(Guid.NewGuid(), "order-process", "order-process:1:abc",
            Guid.NewGuid(), "charge", taskType, vars, Guid.NewGuid(), "ORDER-1");
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
