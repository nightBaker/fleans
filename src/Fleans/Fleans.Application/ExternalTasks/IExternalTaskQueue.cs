using Fleans.Domain.Events;

namespace Fleans.Application.ExternalTasks;

/// <summary>
/// Hands a service task to external workers (#979). One implementation per queue technology —
/// Kafka today (<c>Fleans.Infrastructure/ExternalTasks/KafkaExternalTaskQueue</c>) — chosen by
/// <see cref="ExternalTaskOptions.Provider"/>. <see cref="Publish"/> runs inside the workflow
/// instance's turn: it must not call back into the instance and must give up well inside the 30 s
/// grain-call timeout — the instance then fails the task. Redelivery after that is the worker's
/// side (Kafka: commit after the REST report); workers key side effects on
/// <see cref="ExecuteCustomTaskEvent.ActivityInstanceId"/>.
/// </summary>
public interface IExternalTaskQueue
{
    Task Publish(ExecuteCustomTaskEvent task, CancellationToken cancellationToken);
}
