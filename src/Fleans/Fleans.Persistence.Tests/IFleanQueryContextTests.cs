using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Fleans.Persistence.Tests;

[TestClass]
public class IFleanQueryContextTests
{
    /// <summary>
    /// Reflection-based assertion that the interface's own declaration exposes
    /// no <see cref="Microsoft.EntityFrameworkCore.DbSet{T}"/> properties and no
    /// mutation methods (Add/Update/Remove). The check filters by
    /// <c>DeclaringType == typeof(IFleanQueryContext)</c>: inherited
    /// <see cref="IAsyncDisposable.DisposeAsync"/> is expected and is excluded
    /// from the read-surface contract assertion. Extension methods on
    /// <see cref="IQueryable{T}"/> (e.g. <c>.Include</c>, <c>.Where</c>,
    /// <c>.AsNoTracking</c>) are static and therefore unaffected; consumers
    /// continue to use them on the returned <c>IQueryable&lt;T&gt;</c> values.
    /// See #661.
    /// </summary>
    [TestMethod]
    public void IFleanQueryContext_DoesNotExposeMutationMethods()
    {
        var interfaceType = typeof(IFleanQueryContext);

        foreach (var prop in interfaceType.GetProperties()
                                          .Where(p => p.DeclaringType == interfaceType))
        {
            Assert.IsTrue(
                prop.PropertyType.IsGenericType &&
                prop.PropertyType.GetGenericTypeDefinition() == typeof(IQueryable<>),
                $"IFleanQueryContext.{prop.Name} must be IQueryable<T>, was {prop.PropertyType}");
        }

        var declaredMethods = interfaceType.GetMethods()
            .Where(m => m.DeclaringType == interfaceType && !m.IsSpecialName) // skip property getters
            .ToList();

        Assert.AreEqual(0, declaredMethods.Count,
            "IFleanQueryContext should declare no methods beyond IQueryable<T> property getters " +
            "(inherited IAsyncDisposable.DisposeAsync is expected and excluded by the DeclaringType filter)");
    }
}
