using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

/// <summary>
/// Broker-free checks for the DLQ handler. The end-to-end routing (subscriber failure and
/// deserialization failure → <c>-dlq</c> topic) is covered by
/// <c>Fleans.Application.Tests.KafkaDlqIntegrationTests</c>.
/// </summary>
[TestClass]
public class KafkaDlqFailureHandlerTests
{
    [TestMethod]
    public void ShouldFaultSubsriptionOnError_IsFalse()
    {
        // false keeps the subscription alive: Orleans moves past the poison record and keeps delivering.
        using var publisher = CreatePublisher();
        using var handler = CreateHandler(publisher);

        Assert.IsFalse(handler.ShouldFaultSubsriptionOnError);
    }

    [TestMethod]
    public async Task OnSubscriptionFailure_IsLogOnly()
    {
        using var publisher = CreatePublisher();
        using var handler = CreateHandler(publisher);

        // Would block on the unreachable broker if it tried to re-consume or publish.
        var task = handler.OnSubscriptionFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), new KafkaSequenceToken(1, 0, 0));

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public async Task OnDeliveryFailure_NonKafkaToken_IsDroppedWithoutBrokerCall()
    {
        using var publisher = CreatePublisher();
        using var handler = CreateHandler(publisher);

        var task = handler.OnDeliveryFailure(GuidId.GetNewGuidId(), "provider",
            StreamId.Create("ns", "id"), null!);

        Assert.IsTrue(task.IsCompletedSuccessfully);
        await task;
    }

    [TestMethod]
    public void Dispose_CalledTwice_IsIdempotent()
    {
        using var publisher = CreatePublisher();
        var handler = CreateHandler(publisher);

        handler.Dispose();
        handler.Dispose();
        publisher.Dispose();
        publisher.Dispose();
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

    private static KafkaDlqFailureHandler CreateHandler(KafkaDeadLetterPublisher publisher) =>
        new("fleans-test-0", Options, publisher, NullLoggerFactory.Instance);
}
