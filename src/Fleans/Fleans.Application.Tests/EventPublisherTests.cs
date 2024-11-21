using Fleans.Domain.Activities;
using Fleans.Domain.Sequences;
using Fleans.Domain;
using Orleans.TestingHost;
using Fleans.Application.Events;
using Microsoft.Extensions.Configuration;
using Orleans.Serialization;
using System.Dynamic;

namespace Fleans.Application.Tests
{
    [TestClass]
    public class EventPublisherTests
    {
        private IWorkflowDefinition _workflow = null!;
        private TestCluster _cluster;

        [TestInitialize]
        public void Setup()
        {
            _workflow = CreateSimpleWorkflowWithExclusiveGateway();
            _cluster = CreateCluster();
        }

        [TestMethod]
        public async Task ConsumeEventTest()
        {
            // Arrange
            //             
            var testWF = _cluster.GrainFactory.GetGrain<IWorkflowInstance>(Guid.NewGuid());
                        
            // Act
            await testWF.SetWorkflow(_workflow);
            await testWF.StartWorkflow();

            //TODO test consumer IWorkflowEventsHandler
        }

        private static TestCluster CreateCluster()
        {
            var builder = new TestClusterBuilder();

            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            builder.AddClientBuilderConfigurator<ClientConfiguretor>();

            var cluster = builder.Build();
            cluster.Deploy();
            return cluster;
        }

        private static IWorkflowDefinition CreateSimpleWorkflowWithExclusiveGateway()
        {
            var start = new StartEvent("start");
            var end1 = new EndEvent("end1");
            var end2 = new EndEvent("end2");
            var ifActivity = new ExclusiveGateway("if");

            var workflow = new WorkflowDefinition { WorkflowId = "workflow1", Activities = new List<Domain.Activities.Activity>(), SequenceFlows = new List<SequenceFlow>() };
            workflow.Activities.Add(start);
            workflow.Activities.Add(end1);
            workflow.Activities.Add(end2);
            workflow.Activities.Add(ifActivity);

            workflow.SequenceFlows.Add(new SequenceFlow("seq1", start, ifActivity));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq2", ifActivity, end1, "trueCondition"));
            workflow.SequenceFlows.Add(new ConditionalSequenceFlow("seq3", ifActivity, end2, "falseCondition"));
            return workflow;
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder) =>
               hostBuilder.AddMemoryStreams(WorkflowEventsPublisher.StreamProvider)
                          .AddMemoryGrainStorage("PubSubStore")
                            //.ConfigureServices(services => services.AddSerializer(serializerBuilder =>
                            //{
                            //    serializerBuilder.AddNewtonsoftJsonSerializer(
                            //        isSupported: type => type == typeof(ExpandoObject), new Newtonsoft.Json.JsonSerializerSettings
                            //        {
                            //            TypeNameHandling = Newtonsoft.Json.TypeNameHandling.All
                            //        });
                            //}))
                ;
        }

        private class ClientConfiguretor : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder) =>
              clientBuilder.AddMemoryStreams(WorkflowEventsPublisher.StreamProvider);
        }
    }

  

}