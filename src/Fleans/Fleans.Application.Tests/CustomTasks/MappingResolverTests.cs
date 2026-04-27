using System.Dynamic;
using Fleans.Application.CustomTasks;
using Fleans.Domain.Errors;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class MappingResolverTests
{
    [TestMethod]
    public void BareString_ReturnedAsLiteral()
    {
        var scope = new Dictionary<string, object?> { ["x"] = 42 };
        var result = MappingResolver.Resolve("hello", scope);
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public void TopLevelPath_ReturnsScopeValue()
    {
        var scope = new Dictionary<string, object?> { ["userId"] = 123 };
        var result = MappingResolver.Resolve("=userId", scope);
        Assert.AreEqual(123, result);
    }

    [TestMethod]
    public void NestedPath_WalksDictionariesAndExpandos()
    {
        var inner = new ExpandoObject();
        ((IDictionary<string, object?>)inner)["status"] = "ok";
        var response = new Dictionary<string, object?> { ["body"] = inner };
        var scope = new Dictionary<string, object?> { ["__response"] = response };

        var result = MappingResolver.Resolve("=__response.body.status", scope);
        Assert.AreEqual("ok", result);
    }

    [TestMethod]
    public void MissingPathSegment_ReturnsNull()
    {
        var scope = new Dictionary<string, object?>();
        var result = MappingResolver.Resolve("=missing.value", scope);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void QuotedStringLiteral_ReturnsInnerString()
    {
        var scope = new Dictionary<string, object?>();
        var result = MappingResolver.Resolve("=\"explicit literal\"", scope);
        Assert.AreEqual("explicit literal", result);
    }

    [TestMethod]
    public void IntegerLiteral_ReturnsLong()
    {
        var scope = new Dictionary<string, object?>();
        var result = MappingResolver.Resolve("=42", scope);
        Assert.AreEqual(42L, result);
    }

    [TestMethod]
    public void BooleanAndNullLiterals_Recognized()
    {
        var scope = new Dictionary<string, object?>();
        Assert.AreEqual(true, MappingResolver.Resolve("=true", scope));
        Assert.AreEqual(false, MappingResolver.Resolve("=false", scope));
        Assert.IsNull(MappingResolver.Resolve("=null", scope));
    }

    [TestMethod]
    public void EmptyExpressionAfterEquals_ThrowsCustomTaskFailedActivityException()
    {
        var scope = new Dictionary<string, object?>();
        var ex = Assert.ThrowsExactly<CustomTaskFailedActivityException>(
            () => MappingResolver.Resolve("=", scope));
        Assert.AreEqual(400, ex.GetActivityErrorState().Code);
    }
}
