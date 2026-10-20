using Fleans.Domain.Aggregates;

namespace Fleans.Domain.Tests;

[TestClass]
public class DomainDependencyTests
{
    [TestMethod]
    public void Domain_DoesNotUseLogging()
    {
        // Orleans.Sdk brings Microsoft.Extensions.Logging.Abstractions in transitively, so the
        // package graph cannot keep ILogger out of the Domain; the compiled references can.
        var references = typeof(WorkflowExecution).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToList();

        CollectionAssert.DoesNotContain(references, "Microsoft.Extensions.Logging.Abstractions",
            "Fleans.Domain has no infrastructure concerns — log from the Application layer.");
    }
}
