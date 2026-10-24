using Fleans.Application.Deployments;
using Fleans.Application.Effects;
using Fleans.Application.Grains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Fleans.Application
{
    public static class ApplicationDependencyInjection
    {
        public static void AddApplication(this IServiceCollection services)
        {
            services.AddOptions<WorkflowVariablesOptions>()
                .BindConfiguration(WorkflowVariablesOptions.SectionName)
                .Validate(o => o.MaxSizeBytes > 0, "Fleans:Variables:MaxSizeBytes must be greater than 0.")
                .ValidateOnStart();

            services.AddOptions<WorkerEvaluatorOptions>()
                .BindConfiguration(WorkerEvaluatorOptions.SectionName)
                .Validate(o => o.EvaluatorKeyCount > 0, "Fleans:Worker:EvaluatorKeyCount must be greater than 0.")
                .ValidateOnStart();

            services.AddOptions<DeploymentApprovalOptions>()
                .BindConfiguration(DeploymentApprovalOptions.SectionName)
                .Validate(o => o.RequiredApprovals >= 0, "Fleans:Deployments:RequiredApprovals can't be negative.")
                .ValidateOnStart();

            services.AddOptions<WorkflowInstanceOptions>()
                .BindConfiguration(WorkflowInstanceOptions.SectionName)
                .Validate(o => o.IdleCollectionAge > TimeSpan.FromMinutes(1),
                    "Fleans:Instances:IdleCollectionAge must be longer than Orleans' collection quantum (1 minute).")
                .ValidateOnStart();
            // Waiting instances leave memory after IdleCollectionAge instead of Orleans' 15 minutes.
            services.AddOptions<GrainCollectionOptions>()
                .Configure<IOptions<WorkflowInstanceOptions>>((collection, instances) =>
                    collection.ClassSpecificCollectionAge[typeof(WorkflowInstance).FullName!] = instances.Value.IdleCollectionAge);

            services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
            services.AddSingleton<ICompensationLogService, CompensationLogService>();
            services.AddSingleton<WorkflowDefinitionCache>();

            services.AddSingleton<EffectDispatcher>();
            services.AddSingleton<IEffectHandler, TimerEffectHandler>();
            services.AddSingleton<IEffectHandler, MessageEffectHandler>();
            services.AddSingleton<IEffectHandler, SignalEffectHandler>();
            services.AddSingleton<IEffectHandler, UserTaskEffectHandler>();
            services.AddSingleton<IEffectHandler, WorkflowLifecycleEffectHandler>();
        }
    }
}
