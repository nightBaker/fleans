using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka.Tests;

[TestClass]
public class KafkaAdapterTests
{
    private static Serializer<KafkaBatchContainer> _serializer = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        // AddSerializer() auto-discovers KafkaBatchContainer's [GenerateSerializer] codecs
        // from loaded assemblies — no full silo or TestCluster required.
        var sp = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        _serializer = sp.GetRequiredService<Serializer<KafkaBatchContainer>>();
    }

    [TestMethod]
    public async Task NoEncoder_QueueMessageBatchAsync_ProducesOnce()
    {
        var producer = Substitute.For<IProducer<byte[], byte[]>>();
        var mapper = Substitute.For<IStreamQueueMapper>();
        var adapter = new KafkaQueueAdapter(
            "test", new KafkaStreamingOptions(), mapper, _serializer,
            Substitute.For<ILoggerFactory>(), producer, encoder: null);

        var streamId = StreamId.Create("test-ns", "test-key");
        await adapter.QueueMessageBatchAsync(streamId, new List<string> { "test-event" }, null, null);

        await producer.Received(1).ProduceAsync(Arg.Any<string>(), Arg.Any<Message<byte[], byte[]>>());
    }

    [TestMethod]
    public async Task WithEncoder_ReturnsNull_ProducesOnce()
    {
        var producer = Substitute.For<IProducer<byte[], byte[]>>();
        var mapper = Substitute.For<IStreamQueueMapper>();
        var encoder = Substitute.For<IExternalEventEncoder>();
        encoder.EncodeAsync(Arg.Any<object>()).Returns(Task.FromResult<byte[]?>(null));

        var adapter = new KafkaQueueAdapter(
            "test", new KafkaStreamingOptions(), mapper, _serializer,
            Substitute.For<ILoggerFactory>(), producer, encoder);

        var streamId = StreamId.Create("test-ns", "test-key");
        await adapter.QueueMessageBatchAsync(streamId, new List<string> { "test-event" }, null, null);

        await producer.Received(1).ProduceAsync(Arg.Any<string>(), Arg.Any<Message<byte[], byte[]>>());
    }

    [TestMethod]
    public async Task WithEncoder_ReturnsBytes_ProducesTwice_SecondCallTargetsEventsTopic()
    {
        var producer = Substitute.For<IProducer<byte[], byte[]>>();
        var mapper = Substitute.For<IStreamQueueMapper>();
        var encoder = Substitute.For<IExternalEventEncoder>();
        encoder.EncodeAsync(Arg.Any<object>()).Returns(Task.FromResult<byte[]?>(new byte[] { 0x01 }));

        var adapter = new KafkaQueueAdapter(
            "test", new KafkaStreamingOptions(), mapper, _serializer,
            Substitute.For<ILoggerFactory>(), producer, encoder);

        var streamId = StreamId.Create("test-ns", "test-key");
        await adapter.QueueMessageBatchAsync(streamId, new List<string> { "test-event" }, null, null);

        await producer.Received(2).ProduceAsync(Arg.Any<string>(), Arg.Any<Message<byte[], byte[]>>());

        // Verify the second call targets the events topic (primary topic + "-events").
        var calls = producer.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IProducer<byte[], byte[]>.ProduceAsync))
            .ToList();
        Assert.AreEqual(2, calls.Count);
        StringAssert.EndsWith((string)calls[1].GetArguments()[0], "-events");
    }
}
