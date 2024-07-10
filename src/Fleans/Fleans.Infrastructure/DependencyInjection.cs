using Fleans.Application;
using Fleans.Application.Conditions;
using Fleans.Infrastructure.EventHandlers;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Fleans.Infrastructure
{
    public static class InfrastructureDependencyInjection
    {
        public static void AddInfrastructure(this IServiceCollection services)
        {
            services.AddSingleton<IConditionExpressionEvaluater, DynamicExperessoConditionExpressionEvaluater>();
        }
    }
}
