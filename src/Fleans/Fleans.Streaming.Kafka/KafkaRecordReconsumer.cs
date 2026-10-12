using Confluent.Kafka;
using ErrorCode = Confluent.Kafka.ErrorCode;

namespace Fleans.Streaming.Kafka;

/// <summary>Why <see cref="KafkaRecordReconsumer.FetchAsync"/> could not return the record.</summary>
internal enum KafkaReconsumeOutcome
{
    Found,
    OffsetGone,
    TimedOut,
    Disposed,
}

/// <summary>
/// Re-reads one raw record by topic/partition/offset with an assign-only consumer (never subscribes,
/// never commits). One instance per adapter factory, shared by every queue's delivery-failure handler,
/// so poison records cost one extra Kafka client per silo rather than one per queue.
/// </summary>
internal sealed class KafkaRecordReconsumer : IDisposable
{
    private static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(10);

    private readonly Lazy<IConsumer<byte[], byte[]>> _consumer;

    // Serializes fetches: Assign + Consume on one consumer is not thread-safe. Never disposed, so a
    // fetch racing Dispose observes _disposed instead of an ObjectDisposedException.
    private readonly SemaphoreSlim _lock = new(1, 1);

    private bool _disposed;

    public KafkaRecordReconsumer(KafkaStreamingOptions options)
    {
        _consumer = new Lazy<IConsumer<byte[], byte[]>>(() =>
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = options.Brokers,
                GroupId = $"{options.ConsumerGroup}-dlq-reconsume",
                EnableAutoCommit = false,
                // Error, not Earliest: a purged offset must surface as an error, not silently jump to
                // another record.
                AutoOffsetReset = AutoOffsetReset.Error,
            };
            KafkaClientConfigExtensions.ApplySecurity(config, options);
            var builder = new ConsumerBuilder<byte[], byte[]>(config);
            if (options.OAuthBearerTokenProvider is not null)
                builder.SetOAuthBearerTokenRefreshHandler(options.OAuthBearerTokenProvider);
            return builder.Build();
        });
    }

    /// <summary>Throws only for unexpected consumer errors; a purged offset or timeout is an outcome.</summary>
    public async Task<(KafkaReconsumeOutcome Outcome, Message<byte[], byte[]>? Message)> FetchAsync(
        string topic, int partition, long offset)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return (KafkaReconsumeOutcome.Disposed, null);
            return await Task.Run(() => Fetch(topic, partition, offset)).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private (KafkaReconsumeOutcome, Message<byte[], byte[]>?) Fetch(string topic, int partition, long offset)
    {
        var consumer = _consumer.Value;
        consumer.Assign(new TopicPartitionOffset(topic, partition, offset));
        try
        {
            var result = consumer.Consume(ConsumeTimeout);
            if (result?.Message?.Value is null)
                return (KafkaReconsumeOutcome.TimedOut, null);
            // A compacted offset makes the consumer skip ahead — never dead-letter the wrong record.
            return result.Offset.Value == offset
                ? (KafkaReconsumeOutcome.Found, result.Message)
                : (KafkaReconsumeOutcome.OffsetGone, null);
        }
        catch (ConsumeException ex) when (ex.Error.Code is ErrorCode.Local_AutoOffsetReset
                                              or ErrorCode.OffsetOutOfRange
                                              or ErrorCode.Local_NoOffset)
        {
            return (KafkaReconsumeOutcome.OffsetGone, null);
        }
        finally
        {
            consumer.Unassign();
        }
    }

    public void Dispose()
    {
        // Waits for an in-flight fetch so the native consumer is never disposed mid-Consume.
        _lock.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_consumer.IsValueCreated)
                _consumer.Value.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }
}
