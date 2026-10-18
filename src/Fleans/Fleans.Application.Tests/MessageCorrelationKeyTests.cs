using Fleans.Application.Grains;

namespace Fleans.Application.Tests;

[TestClass]
public class MessageCorrelationKeyTests
{
    [TestMethod]
    public void Build_SimpleKey_ReturnsCompositeKey()
    {
        var result = MessageCorrelationKey.Build("paymentReceived", "order-123");
        Assert.AreEqual("paymentReceived/order-123", result);
    }

    [TestMethod]
    public void Build_KeyWithSlash_EncodesSlash()
    {
        var result = MessageCorrelationKey.Build("msg", "region/order-123");
        Assert.AreEqual("msg/region%2Forder-123", result);
    }

    [TestMethod]
    public void Build_KeyWithSpecialChars_EncodesCorrectly()
    {
        var result = MessageCorrelationKey.Build("msg", "key?a=1&b=2");
        Assert.AreEqual("msg/key%3Fa%3D1%26b%3D2", result);
    }

    [TestMethod]
    public void Build_UnicodeKey_EncodesCorrectly()
    {
        var result = MessageCorrelationKey.Build("msg", "заказ-123");
        // Uri.EscapeDataString encodes unicode
        StringAssert.StartsWith(result, "msg/");
        Assert.AreNotEqual("msg/заказ-123", result);
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
