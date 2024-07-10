using Fleans.Infrastructure.EventHandlers;
using System.Dynamic;

namespace Fleans.Infrastructure.Tests
{
    [TestClass]
    public class DynamicExperessoConditionExpressionEvaluaterTests
    {
        [TestMethod]
        public async Task ExpressionEval_Should_Return_TrueAsync()
        {
            // Arrange
            
            var evaluater = new DynamicExperessoConditionExpressionEvaluater();
            dynamic expando = new ExpandoObject();
            expando.x = 6;
            expando.y = 5;

            // Act
            var result = await evaluater.Evaluate("_context.x > _context.y",
                    expando);

            // Assert
            Assert.IsTrue(result);
        }
    }
}