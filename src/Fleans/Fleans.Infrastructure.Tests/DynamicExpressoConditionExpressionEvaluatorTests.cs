using System.Dynamic;
using Fleans.Infrastructure.Conditions;

namespace Fleans.Infrastructure.Tests
{
    [TestClass]
    public class DynamicExpressoConditionExpressionEvaluatorTests
    {

        [TestMethod]
        public async Task Evaluate_SameExpression_DifferentVariables_ReturnsDifferentResults()
        {
            // Arrange
            var evaluator = new DynamicExpressoConditionExpressionEvaluator();
            const string expression = "_context.x > 5";

            dynamic vars1 = new ExpandoObject();
            vars1.x = 10;

            dynamic vars2 = new ExpandoObject();
            vars2.x = 3;

            // Act
            var result1 = await evaluator.Evaluate(expression, vars1);
            var result2 = await evaluator.Evaluate(expression, vars2);

            // Assert
            Assert.IsTrue(result1);
            Assert.IsFalse(result2);
        }

        [TestMethod]
        public async Task Evaluate_ConcurrentCalls_SameExpression_AllReturnCorrectResults()
        {
            // Arrange
            var evaluator = new DynamicExpressoConditionExpressionEvaluator();
            const string expression = "_context.value > 25";
            const int concurrency = 50;

            // Act
            var tasks = Enumerable.Range(0, concurrency).Select(i =>
            {
                dynamic vars = new ExpandoObject();
                ((IDictionary<string, object>)vars)["value"] = i;
                return evaluator.Evaluate(expression, (ExpandoObject)vars);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            // Assert — values 26..49 should be true, 0..25 should be false
            for (int i = 0; i < concurrency; i++)
            {
                Assert.AreEqual(i > 25, results[i], $"Failed for value {i}");
            }
        }

        [TestMethod]
        public async Task Compile_ValidCondition_ThenEvaluateUsesIt()
        {
            // Arrange
            var evaluator = new DynamicExpressoConditionExpressionEvaluator();
            dynamic vars = new ExpandoObject();
            vars.x = 10;

            // Act
            evaluator.Compile("_context.x > 5");
            var result = await evaluator.Evaluate("_context.x > 5", vars);

            // Assert
            Assert.IsTrue(result);
        }

        [TestMethod]
        public void Compile_SyntaxError_Throws()
        {
            // Arrange
            var evaluator = new DynamicExpressoConditionExpressionEvaluator();

            // Act & Assert
            Assert.ThrowsExactly<DynamicExpresso.Exceptions.ParseException>(() => evaluator.Compile("_context.x > (5"));
        }

        [TestMethod]
        public void Compile_NonBooleanCondition_Throws()
        {
            // Arrange
            var evaluator = new DynamicExpressoConditionExpressionEvaluator();

            // Act & Assert — Evaluate would fail its cast to bool on every instance
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => evaluator.Compile("1 + 2"));
            StringAssert.Contains(ex.Message, "Int32");
        }
    }
}