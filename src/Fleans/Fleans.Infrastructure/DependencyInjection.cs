using Fleans.Application.Conditions;
using Fleans.Domain;
using Fleans.Application.Scripts;
using Fleans.Application.Bpmn;
using Fleans.Infrastructure.Bpmn;
using Fleans.Infrastructure.Conditions;
using Fleans.Infrastructure.Scripts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fleans.Infrastructure
{
    public static class InfrastructureDependencyInjection
    {
        public static void AddInfrastructure(this IServiceCollection services)
        {
            services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();
            services.AddSingleton<IMappingExpressionEvaluator, DynamicExpressoMappingExpressionEvaluator>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IScriptEngine, DynamicExpressoScriptEngine>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IScriptEngine, JavaScriptScriptEngine>());
            services.AddSingleton<IScriptExpressionExecutor, ScriptExpressionExecutor>();
            services.AddSingleton<IBpmnConverter, BpmnConverter>();
        }
    }
}
