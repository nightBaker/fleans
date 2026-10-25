using Confluent.Kafka;

namespace Fleans.Streaming.Kafka.Tests;

[TestClass]
public class KafkaConsumerLagTests
{
    [TestMethod]
    public void Lag_IsTheRecordsPastTheLastOneRead()
    {
        // Arrange — the high watermark is the offset the next produced record gets
        var watermarks = new WatermarkOffsets(new Offset(0), new Offset(100));

        // Act
        var lag = KafkaConsumerLag.Compute(watermarks, lastReadOffset: 89);

        // Assert — records 90 … 99 are still unread
        Assert.AreEqual(10L, lag);
    }

    [TestMethod]
    public void Lag_AtTheHead_IsZero()
    {
        var watermarks = new WatermarkOffsets(new Offset(0), new Offset(100));

        Assert.AreEqual(0L, KafkaConsumerLag.Compute(watermarks, lastReadOffset: 99));
    }

    [TestMethod]
    public void Lag_WithAStaleCachedWatermark_IsNeverNegative()
    {
        // The cached watermark can trail the record just read until the next fetch response.
        var watermarks = new WatermarkOffsets(new Offset(0), new Offset(100));

        Assert.AreEqual(0L, KafkaConsumerLag.Compute(watermarks, lastReadOffset: 120));
    }

    [TestMethod]
    public void Lag_WithoutAWatermarkYet_IsNotReported()
    {
        var watermarks = new WatermarkOffsets(Offset.Unset, Offset.Unset);

        Assert.IsNull(KafkaConsumerLag.Compute(watermarks, lastReadOffset: 5));
    }
}
