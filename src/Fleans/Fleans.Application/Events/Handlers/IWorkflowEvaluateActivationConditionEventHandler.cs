namespace Fleans.Application.Events.Handlers;

public interface IWorkflowEvaluateActivationConditionEventHandler : IGrainWithStringKey
{
    void Ping();
}
