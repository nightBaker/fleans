using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Streams;

namespace Fleans.Streaming.Kafka;

internal sealed class KafkaQueueAdapter : IQueueAdapter, IDisposable
{
    private readonly KafkaStreamingOptions _options;
    private readonly IStreamQueueMapper _mapper;
    private readonly Serializer<KafkaBatchContainer> _serializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<KafkaQueueAdapter> _logger;
    private readonly IProducer<byte[], byte[]> _producer;

    public string Name { get; }

    public bool IsRewindable => false;

    public StreamProviderDirection Direction => StreamProviderDirection.ReadWrite;

    public KafkaQueueAdapter(
        string name,
        KafkaStreamingOptions options,
        IStreamQueueMapper mapper,
        Serializer<KafkaBatchContainer> serializer,
        ILoggerFactory loggerFactory)
    {
        Name = name;
        _options = options;
        _mapper = mapper;
        _serializer = serializer;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<KafkaQueueAdapter>();

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = _options.Brokers,
            EnableIdempotence = false,
            Acks = Acks.All,
        };
        _producer = new ProducerBuilder<byte[], byte[]>(producerConfig).Build();
    }

    public async Task QueueMessageBatchAsync<T>(
        StreamId streamId,
        IEnumerable<T> events,
        StreamSequenceToken token,
        Dictionary<string, object> requestContext)
    {
        if (token is not null)
        {
            throw new ArgumentException(
                "Kafka adapter does not support enqueueing with explicit StreamSequenceToken.",
                nameof(token));
        }

        var batch = new KafkaBatchContainer
        {
            StreamId = streamId,
            Events = events.Cast<object>().ToList(),
            RequestContext = requestContext,
        };

        var bytes = _serializer.SerializeToArray(batch);
        var queueId = _mapper.GetQueueForStream(streamId);
        var topic = KafkaTopicNaming.TopicForQueue(_options, queueId);
        var key = streamId.ToString().AsSpan().ToArray() is { } _
            ? System.Text.Encoding.UTF8.GetBytes(streamId.ToString())
            : Array.Empty<byte>();

        try
        {
            await _producer.ProduceAsync(topic, new Message<byte[], byte[]>
            {
                Key = key,
                Value = bytes,
            }).ConfigureAwait(false);
        }
        catch (ProduceException<byte[], byte[]> ex)
        {
            _logger.LogError(ex, "Failed to publish batch to Kafka topic {Topic}", topic);
            throw;
        }
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId) =>
        new KafkaQueueAdapterReceiver(queueId, _options, _serializer, _loggerFactory);

    public void Dispose()
    {
        try
        {
            _producer.Flush(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // best-effort flush during teardown
        }
        _producer.Dispose();
    }
}
