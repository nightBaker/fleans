using Fleans.Application.Grains;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageCorrelationKeyTests
{
    [TestMethod]
    [DataRow("paymentReceived", "order-123", "paymentReceived/order-123", DisplayName = "Plain key")]
    [DataRow("msg", "region/order-123", "msg/region%2Forder-123", DisplayName = "Slash is escaped")]
    [DataRow("msg", "key?a=1&b=2", "msg/key%3Fa%3D1%26b%3D2", DisplayName = "Query characters are escaped")]
    [DataRow("msg", "заказ-123", "msg/%D0%B7%D0%B0%D0%BA%D0%B0%D0%B7-123", DisplayName = "Unicode is escaped")]
    public void Build_EscapesCorrelationKey(string messageName, string correlationKey, string expected)
    {
        Assert.AreEqual(expected, MessageCorrelationKey.Build(messageName, correlationKey));
    }

    [TestMethod]
    public void Build_CompositeOverMaxLength_Throws()
    {
        var correlationKey = new string('k', MessageCorrelationKey.MaxLength - "msg/".Length + 1);

        Assert.ThrowsExactly<ArgumentException>(() => MessageCorrelationKey.Build("msg", correlationKey));
    }

    [TestMethod]
    public void Build_CompositeAtMaxLength_Succeeds()
    {
        var correlationKey = new string('k', MessageCorrelationKey.MaxLength - "msg/".Length);

        Assert.AreEqual(MessageCorrelationKey.MaxLength, MessageCorrelationKey.Build("msg", correlationKey).Length);
    }

    [TestMethod]
    public void TryBuild_CompositeOverMaxLength_ReturnsNull()
    {
        var correlationKey = new string('k', MessageCorrelationKey.MaxLength);

        Assert.IsNull(MessageCorrelationKey.TryBuild("msg", correlationKey));
    }

    [TestMethod]
    public void Parse_ReturnsMessageNameAndUnescapedCorrelationKey()
    {
        var key = MessageCorrelationKey.Build("orders/payment", "region/order 123");

        var (messageName, correlationKey) = MessageCorrelationKey.Parse(key);

        Assert.AreEqual("orders/payment", messageName);
        Assert.AreEqual("region/order 123", correlationKey);
    }

    [TestMethod]
    public void MessageNameOf_ReturnsNamePart_OrTheInputWhenThereIsNoSeparator()
    {
        Assert.AreEqual("orders/payment", MessageCorrelationKey.MessageNameOf("orders/payment/order%201"));
        Assert.AreEqual("malformed", MessageCorrelationKey.MessageNameOf("malformed"));
    }
}
