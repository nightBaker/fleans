using Fleans.Application.CustomTasks;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class ReconcileGuardTests
{
    [TestMethod]
    public void IsAnomalousEmptyAliveSilos_ZeroSilosWithEntries_ReturnsTrue()
    {
        Assert.IsTrue(ReconcileGuard.IsAnomalousEmptyAliveSilos(aliveSilosCount: 0, currentEntryCount: 3));
    }

    [TestMethod]
    public void IsAnomalousEmptyAliveSilos_ZeroSilosWithZeroEntries_ReturnsFalse()
    {
        Assert.IsFalse(ReconcileGuard.IsAnomalousEmptyAliveSilos(aliveSilosCount: 0, currentEntryCount: 0));
    }

    [TestMethod]
    public void IsAnomalousEmptyAliveSilos_SomeSilosWithEntries_ReturnsFalse()
    {
        Assert.IsFalse(ReconcileGuard.IsAnomalousEmptyAliveSilos(aliveSilosCount: 2, currentEntryCount: 3));
    }

    [TestMethod]
    public void IsAnomalousEmptyAliveSilos_SomeSilosWithZeroEntries_ReturnsFalse()
    {
        Assert.IsFalse(ReconcileGuard.IsAnomalousEmptyAliveSilos(aliveSilosCount: 2, currentEntryCount: 0));
    }
}
