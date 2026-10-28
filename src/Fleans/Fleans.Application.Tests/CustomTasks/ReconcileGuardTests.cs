using Fleans.Application.CustomTasks;

namespace Fleans.Application.Tests.CustomTasks;

[TestClass]
public class ReconcileGuardTests
{
    [TestMethod]
    [DataRow(0, 3, true, DisplayName = "No alive silos but entries exist: anomalous")]
    [DataRow(0, 0, false, DisplayName = "No alive silos, no entries")]
    [DataRow(2, 3, false, DisplayName = "Alive silos with entries")]
    [DataRow(2, 0, false, DisplayName = "Alive silos, no entries")]
    public void IsAnomalousEmptyAliveSilos(int aliveSilosCount, int currentEntryCount, bool expected)
    {
        Assert.AreEqual(expected,
            ReconcileGuard.IsAnomalousEmptyAliveSilos(aliveSilosCount, currentEntryCount));
    }
}
