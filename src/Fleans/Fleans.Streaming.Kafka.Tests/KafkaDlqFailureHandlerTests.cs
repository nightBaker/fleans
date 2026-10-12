using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Providers.Streams.Common;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

/// <summary>
/// Broker-free checks for the DLQ handler. The end-to-end routing (subscriber failure and
/// deserialization failure → skipped, not dead-lettered) is covered by
/// <c>Fleans.Application.Tests.KafkaDlqIntegrationTests</c>.
/// </summary>
[TestClass]
public class KafkaDlqFailureHandlerTests
{
    [TestMethod]
    public void ShouldFaultSubsriptionOnError_IsFalse()
    {
        // false keeps the subscription alive: Orleans moves past the poison record and keeps delivering.
        using var reconsumer = new KafkaRecordReconsumer(Options);
        using var publisher = CreatePublisher();
        var handler = CreateHandler(reconsumer, publisher);

        Assert.IsFalse(handler.ShouldFaultSubsriptionOnError);
    }

    [TestMethod]
    public async Task OnSubscriptionFailure_IsLogOnly()
    {
        using var reconsumer = new KafkaRecordReconsumer(Options);
        using var publisher = CreatePublisher();
        var handler = CreateHandler(reconsumer, publisher);

        // Would block on the unreachable broker if it tried to re-consume or publish.
        var task = handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), new KafkaSequenceToken(1, 0, 0));

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public async Task OnDeliveryFailure_NonKafkaToken_IsDroppedWithoutBrokerCall()
    {
        using var reconsumer = new KafkaRecordReconsumer(Options);
        using var publisher = CreatePublisher();
        var handler = CreateHandler(reconsumer, publisher);

        var task = handler.OnDeliveryFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), null!);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        var reconsumer = new KafkaRecordReconsumer(Options);
        var publisher = CreatePublisher();

        reconsumer.Dispose();
        reconsumer.Dispose();
        publisher.Dispose();
        publisher.Dispose();
    }

    [TestMethod]
    public async Task OnDeliveryFailure_AfterShutdown_IsDroppedWithoutBrokerCall()
    {
        // Arrange — a late failure after the factory released its Kafka clients.
        var reconsumer = new KafkaRecordReconsumer(Options);
        var publisher = CreatePublisher();
        var handler = CreateHandler(reconsumer, publisher);
        reconsumer.Dispose();
        publisher.Dispose();

        // Act — would throw ObjectDisposedException or block on the unreachable broker if unguarded.
        var task = handler.OnDeliveryFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), new KafkaSequenceToken(1, 0, 0));
        await task.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public async Task PublishAsync_AfterDispose_IsDroppedWithoutBrokerCall()
    {
        var publisher = CreatePublisher();
        publisher.Dispose();

        var task = publisher.PublishAsync("t-0", 0, 1, null, [1], KafkaDeadLetterReason.DeliveryFailed);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public async Task GetDeliveryFailureHandler_AfterFactoryDispose_ReturnsNoOpHandler()
    {
        // Arrange
        var factory = new KafkaQueueAdapterFactory("provider", Options, new SimpleQueueCacheOptions(),
            null!, NullLoggerFactory.Instance);
        var queueId = QueueId.GetQueueId("t", 0, 0);
        var before = await factory.GetDeliveryFailureHandler(queueId);

        // Act
        factory.Dispose();
        var after = await factory.GetDeliveryFailureHandler(queueId);

        // Assert
        Assert.IsInstanceOfType<KafkaDlqFailureHandler>(before);
        Assert.IsInstanceOfType<NoOpStreamDeliveryFailureHandler>(after);
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
    public async Task PublisherDispose_WithLiveLease_DefersProducerDisposeToLastRelease()
    {
        // Arrange — a handler holds a lease between re-consume and publish, longer than Dispose waits.
        var publisher = CreatePublisher();
        var lease = publisher.TryAcquire();
        Assert.IsNotNull(lease);

        // Act
        await Task.Run(publisher.Dispose).WaitAsync(TimeSpan.FromSeconds(10)); // bounded: returns despite the lease
        var disposedUnderLease = publisher.IsProducerDisposed;
        var newLease = publisher.TryAcquire();
        lease.Dispose();

        // Assert
        Assert.IsFalse(disposedUnderLease, "the producer must stay open while a lease is live");
        Assert.IsNull(newLease, "no new leases after Dispose");
        Assert.IsTrue(publisher.IsProducerDisposed, "the last lease released disposes the producer");
    }

    [TestMethod]
    public async Task GetDeliveryFailureHandler_DoesNotCreateDlqProducer()
    {
        var factory = new KafkaQueueAdapterFactory("provider", Options, new SimpleQueueCacheOptions(),
            null!, NullLoggerFactory.Instance);

        await factory.GetDeliveryFailureHandler(QueueId.GetQueueId("t", 0, 0));

        Assert.IsFalse(factory.IsDeadLetterPublisherCreated, "the DLQ producer is built on the first failure, not at queue init");
        factory.Dispose();
    }

    [TestMethod]
    public void OptionsValidator_FailsInvalidNamedOptions_AndSkipsOtherNames()
    {
        var validator = new KafkaStreamingOptionsValidator("provider");
        var invalid = new KafkaStreamingOptions { DeliveryRetryWindow = TimeSpan.Zero };

        Assert.IsTrue(validator.Validate("provider", invalid).Failed);
        Assert.IsTrue(validator.Validate("other", invalid).Skipped);
        Assert.IsTrue(validator.Validate("provider", new KafkaStreamingOptions()).Succeeded);
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

    private static KafkaDeadLetterPublisher CreatePublisher() => new(Options, NullLoggerFactory.Instance);

    private static KafkaDlqFailureHandler CreateHandler(KafkaRecordReconsumer reconsumer, KafkaDeadLetterPublisher publisher) =>
        new("fleans-test-0", reconsumer, () => publisher, NullLoggerFactory.Instance);
}
