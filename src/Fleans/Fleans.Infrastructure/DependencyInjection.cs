using Fleans.Application.Conditions;
using Fleans.Application.Scripts;
using Fleans.Domain.Persistence;
using Fleans.Infrastructure.Bpmn;
using Fleans.Infrastructure.Conditions;
using Fleans.Infrastructure.Scripts;
using Fleans.Infrastructure.Storage;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace Fleans.Infrastructure
{
    public static class InfrastructureDependencyInjection
    {
        public static void AddInfrastructure(this IServiceCollection services)
        {
            services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();
            services.AddSingleton<IScriptExpressionExecutor, DynamicExpressoScriptExpressionExecutor>();
            services.AddSingleton<IBpmnConverter, BpmnConverter>();
            services.AddSingleton<IProcessDefinitionRepository, InMemoryProcessDefinitionRepository>();
            services.AddKeyedSingleton<IGrainStorage>("workflowInstances", (_, _) => new WorkflowInstanceGrainStorage());
            services.AddKeyedSingleton<IGrainStorage>("activityInstances", (_, _) => new ActivityInstanceGrainStorage());
        }
    }
}
