using System.Dynamic;
using Fleans.Infrastructure.Conditions;

namespace Fleans.Infrastructure.Tests
{
    [TestClass]
    public class DynamicExpressoConditionExpressionEvaluatorTests
    {
        [TestMethod]
        public async Task ExpressionEval_Should_Return_TrueAsync()
        {
            // Arrange

            var evaluator = new DynamicExpressoConditionExpressionEvaluator();
            dynamic expando = new ExpandoObject();
            expando.x = 6;
            expando.y = 5;

            // Act
            var result = await evaluator.Evaluate("_context.x > _context.y",
                    expando);

            // Assert
            Assert.IsTrue(result);
        }
    }
}