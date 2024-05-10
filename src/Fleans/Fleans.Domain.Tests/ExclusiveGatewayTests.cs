using System.Diagnostics;

namespace Fleans.Domain.Tests
{
    [TestClass]
    public class ExclusiveGatewayTests
    {
        [TestMethod]
        public async Task IfStatement_ShouldRun_ThenBranchNotElse()
        {
            // Arrange
            

            // Act
            //await workflow.Run();

            //// Assert
            //_ = activity.Received(1).ExecuteAsync();
            //_ = elseActivity.Received(0).ExecuteAsync();
        }

        [TestMethod]
        public async Task IfStatement_ShouldRun_ElseBranchNotThen()
        {
            // Arrange
           
            //// Act
            //await workflow.Run();

            //// Assert
            //_ = activity.Received(0).ExecuteAsync();
            //_ = elseActivity.Received(1).ExecuteAsync();
        }
    }
}