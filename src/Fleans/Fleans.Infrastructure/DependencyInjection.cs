using Fleans.Application;
using Fleans.Application.Conditions;
using Fleans.Infrastructure.Bpmn;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Fleans.Infrastructure.Conditions;

namespace Fleans.Infrastructure
{
    public static class InfrastructureDependencyInjection
    {
        public static void AddInfrastructure(this IServiceCollection services)
        {
            services.AddSingleton<IConditionExpressionEvaluator, DynamicExpressoConditionExpressionEvaluator>();
            services.AddSingleton<IBpmnConverter, BpmnConverter>();
        }
    }
}
