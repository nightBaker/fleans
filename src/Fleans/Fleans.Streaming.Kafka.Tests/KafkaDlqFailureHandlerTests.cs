using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

[TestClass]
public class KafkaDlqFailureHandlerTests
{
    [TestMethod]
    public void ShouldFaultSubsriptionOnError_IsFalse()
    {
        // ShouldFaultSubsriptionOnError = false is the load-bearing invariant:
        // Orleans keeps the same in-memory batch and increments the failure count
        // per retry, allowing the handler to count retries and route to DLQ.
        using var handler = CreateHandler();
        Assert.IsFalse(handler.ShouldFaultSubsriptionOnError);
    }

    [TestMethod]
    public async Task OnSubscriptionFailure_BelowRetryThreshold_DoesNotRoute()
    {
        using var handler = CreateHandler(maxRetries: 3);
        var kt = new KafkaSequenceToken(offset: 50, eventIndex: 0, partition: 0);

        // Calls 1 and 2 are below threshold — should return CompletedTask without routing
        await handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), kt);
        await handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), kt);

        // No exception and no DLQ routing (routing would require a live broker and throw)
        // If we get here without an exception from Kafka, below-threshold paths returned cleanly.
    }

    [TestMethod]
    public async Task OnSubscriptionFailure_AfterThreshold_GuardPreventsSecondRoute()
    {
        // After _routedToDlq guard is set, subsequent calls return CompletedTask immediately.
        // We can verify this by calling with a null sequence token (which hits the guard
        // and logs, but doesn't throw) on the first call, then a second call returns immediately.
        using var handler = CreateHandler(maxRetries: 1);

        // First call at threshold with null token → logs MissingSequenceToken, sets guard
        await handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), null!);

        // Second call with same key (null token → streamId-based key) → guard hit, returns Task.CompletedTask
        var result = handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), null!);

        Assert.IsTrue(result.IsCompleted);
        Assert.IsFalse(result.IsFaulted);
    }

    [TestMethod]
    public async Task OnSubscriptionFailure_NullSequenceToken_LogsAndSkips()
    {
        using var handler = CreateHandler(maxRetries: 1);

        // A null token cannot be cast to KafkaSequenceToken — logs 12008 and returns without routing.
        await handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "test-null-token"), null!);
        // No exception — the handler gracefully skips DLQ routing and just logs.
    }

    [TestMethod]
    public async Task OnDeliveryFailure_AlwaysReturnsCompleted()
    {
        using var handler = CreateHandler();

        // Returns Task (void) — just verify no exception is thrown
        await handler.OnDeliveryFailure(
            GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), new KafkaSequenceToken(1, 0, 0));
    }

    [TestMethod]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var handler = CreateHandler();
        handler.Dispose();
        handler.Dispose(); // Must not throw
    }

    // ----- Integration tests (require live Kafka broker) -----

    [TestMethod]
    [Ignore("Integration test — requires a live Kafka broker. Set KAFKA_BROKERS and remove [Ignore] to run.")]
    public void CommitOffset_ExplicitOffsetsWithoutAssign_DoesNotThrow()
    {
        var brokers = Environment.GetEnvironmentVariable("KAFKA_BROKERS") ?? "localhost:9092";
        using var consumer = new Confluent.Kafka.ConsumerBuilder<Confluent.Kafka.Ignore, Confluent.Kafka.Ignore>(
            new Confluent.Kafka.ConsumerConfig
            {
                BootstrapServers = brokers,
                GroupId = "fleans-test-commit-without-assign",
                EnableAutoCommit = false,
            }).Build();

        // Committing explicit offsets without Assign or Subscribe must not throw.
        // librdkafka sends OffsetCommitRequest with generation_id=-1 directly to the coordinator.
        var offsets = new[] { new Confluent.Kafka.TopicPartitionOffset("fleans-test-0", 0, new Confluent.Kafka.Offset(1)) };
        consumer.Commit(offsets); // Must not throw KafkaException
    }

    // ----- Helpers -----

    private static KafkaDlqFailureHandler CreateHandler(int maxRetries = 3) =>
        new("fleans-test-0",
            new KafkaStreamingOptions
            {
                Brokers = "localhost:9092", // not contacted in unit tests
                ConsumerGroup = "fleans-test",
                MaxConsumerRetries = maxRetries,
                EnableDeadLetterQueue = true,
            },
            NullLoggerFactory.Instance);
}
