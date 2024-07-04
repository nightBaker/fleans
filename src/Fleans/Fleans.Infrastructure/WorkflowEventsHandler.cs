using Fleans.Application.Events;
using Fleans.Domain.Events;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Streams;

namespace Fleans.Application.Events.Handlers
{
    [ImplicitStreamSubscription(WorkflowEventsPublisher.StreamNameSpace)]
    public class ReceiverGrain : Grain, IWorkflowEventsHandler
    {

        private readonly ILogger<ReceiverGrain> _logger;

        public ReceiverGrain(ILogger<ReceiverGrain> logger)
        {
            _logger = logger;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            var streamProvider = this.GetStreamProvider(WorkflowEventsPublisher.StreamProvider);
            var streamId = StreamId.Create(WorkflowEventsPublisher.StreamNameSpace, nameof(EvaluateConditionEvent));
            var stream = streamProvider.GetStream<EvaluateConditionEvent>(streamId);

            await stream.SubscribeAsync(OnNextAsync, OnErrorAsync, OnCompletedAsync);

            await base.OnActivateAsync(cancellationToken);
        }

        public Task OnNextAsync(EvaluateConditionEvent item, StreamSequenceToken? token = null)
        {
            _logger.LogInformation("OnNextAsync({Item}{Token})", item, token != null ? token.ToString() : "null");

            return Task.CompletedTask;
        }

        public Task OnCompletedAsync()
        {
            _logger.LogInformation("OnCompletedAsync()");
            return Task.CompletedTask;
        }

        public Task OnErrorAsync(Exception ex)
        {
            _logger.LogInformation(ex, "OnErrorAsync()");

            return Task.CompletedTask;
        }

        public void Ping()
        {
            
        }
    }
}
