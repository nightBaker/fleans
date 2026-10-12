using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

/// <summary>
/// Broker-free checks for the DLQ handler and its options. The end-to-end routing (subscriber failure →
/// <c>-dlq</c> topic; deserialization failure → skipped, not dead-lettered) is covered by
/// <c>Fleans.Application.Tests.KafkaDlqIntegrationTests</c>.
/// </summary>
[TestClass]
public class KafkaDlqFailureHandlerTests
{
    [TestMethod]
    public void ShouldFaultSubsriptionOnError_IsFalse()
    {
        // false keeps the subscription alive: Orleans moves past the poison record and keeps delivering.
        using var adapter = CreateAdapter();

        Assert.IsFalse(CreateHandler(adapter).ShouldFaultSubsriptionOnError);
    }

    [TestMethod]
    public async Task OnSubscriptionFailure_IsLogOnly()
    {
        using var adapter = CreateAdapter();

        // Would block on the unreachable broker if it tried to re-consume or publish.
        var task = CreateHandler(adapter).OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), new KafkaSequenceToken(1, 0, 0));

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public async Task OnDeliveryFailure_NonKafkaToken_IsDroppedWithoutBrokerCall()
    {
        using var adapter = CreateAdapter();

        var task = CreateHandler(adapter).OnDeliveryFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), null!);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public async Task GetDeliveryFailureHandler_DlqDisabled_ReturnsNoOpHandler()
    {
        var options = new KafkaStreamingOptions { EnableDeadLetterQueue = false };
        var factory = new KafkaQueueAdapterFactory("provider", options, new SimpleQueueCacheOptions(),
            null!, NullLoggerFactory.Instance);

        var handler = await factory.GetDeliveryFailureHandler(QueueId.GetQueueId("t", 0, 0));

        Assert.IsInstanceOfType<NoOpStreamDeliveryFailureHandler>(handler);
    }

    [TestMethod]
    public async Task GetDeliveryFailureHandler_DlqEnabledWithAdapter_ReturnsDlqHandler()
    {
        using var adapter = CreateAdapter();
        var factory = new KafkaQueueAdapterFactory("provider", Options, new SimpleQueueCacheOptions(),
            null!, NullLoggerFactory.Instance) { Adapter = adapter };

        var handler = await factory.GetDeliveryFailureHandler(QueueId.GetQueueId("t", 0, 0));

        Assert.IsInstanceOfType<KafkaDlqFailureHandler>(handler);
    }

    [TestMethod]
    public async Task GetDeliveryFailureHandler_DlqEnabledBeforeAdapter_FallsBackToNoOp()
    {
        // Logged (EventId 11112) — the ordering assumption is checked, not silent.
        var factory = new KafkaQueueAdapterFactory("provider", Options, new SimpleQueueCacheOptions(),
            null!, NullLoggerFactory.Instance);

        var handler = await factory.GetDeliveryFailureHandler(QueueId.GetQueueId("t", 0, 0));

        Assert.IsInstanceOfType<NoOpStreamDeliveryFailureHandler>(handler);
    }

    [TestMethod]
    public void ValidateOptions_NonPositiveDeliveryRetryWindow_Throws()
    {
        var options = new KafkaStreamingOptions { DeliveryRetryWindow = TimeSpan.Zero };

        Assert.ThrowsExactly<InvalidOperationException>(() => KafkaQueueAdapterFactory.ValidateOptions(options));
    }

    [TestMethod]
    public void ValidateOptions_EmptySuffixWithDlqEnabled_Throws()
    {
        var options = new KafkaStreamingOptions { EnableDeadLetterQueue = true, DeadLetterTopicSuffix = "" };

        Assert.ThrowsExactly<InvalidOperationException>(() => KafkaQueueAdapterFactory.ValidateOptions(options));
    }

    [TestMethod]
    public void ValidateOptions_EmptySuffixWithDlqDisabled_DoesNotThrow()
    {
        var options = new KafkaStreamingOptions { EnableDeadLetterQueue = false, DeadLetterTopicSuffix = "" };

        KafkaQueueAdapterFactory.ValidateOptions(options);
    }

    [TestMethod]
    public void ValidateOptions_SuffixMakingDlqTopicALiveQueueTopic_Throws()
    {
        // fleans-1 + "0" = fleans-10, which is queue 10's source topic.
        var options = new KafkaStreamingOptions
        {
            TopicPrefix = "fleans-", QueueCount = 12, EnableDeadLetterQueue = true, DeadLetterTopicSuffix = "0",
        };

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => KafkaQueueAdapterFactory.ValidateOptions(options));
        StringAssert.Contains(ex.Message, "fleans-10");
    }

    [TestMethod]
    public void ValidateOptions_DefaultSuffixWithManyQueues_DoesNotThrow()
    {
        var options = new KafkaStreamingOptions { QueueCount = 64, EnableDeadLetterQueue = true };

        KafkaQueueAdapterFactory.ValidateOptions(options);
    }

    [TestMethod]
    public void AllExpectedTopics_IncludesDlqTopicsOnlyWhenEnabled()
    {
        var disabled = new KafkaStreamingOptions { TopicPrefix = "t-", QueueCount = 2 };
        var enabled = new KafkaStreamingOptions { TopicPrefix = "t-", QueueCount = 2, EnableDeadLetterQueue = true };

        CollectionAssert.AreEqual(new[] { "t-0", "t-1" }, KafkaTopicNaming.AllExpectedTopics(disabled).ToArray());
        CollectionAssert.AreEqual(new[] { "t-0", "t-0-dlq", "t-1", "t-1-dlq" }, KafkaTopicNaming.AllExpectedTopics(enabled).ToArray());
    }

    private static readonly KafkaStreamingOptions Options = new()
    {
        Brokers = "localhost:1", // never contacted by these tests
        ConsumerGroup = "fleans-test",
        EnableDeadLetterQueue = true,
    };

    private static KafkaQueueAdapter CreateAdapter() =>
        new("provider", Options, null!, null!, NullLoggerFactory.Instance);

    private static KafkaDlqFailureHandler CreateHandler(KafkaQueueAdapter adapter) =>
        new("fleans-test-0", Options, adapter, NullLoggerFactory.Instance);
}
