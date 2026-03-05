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
}
