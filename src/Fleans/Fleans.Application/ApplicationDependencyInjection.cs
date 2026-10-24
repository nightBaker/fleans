using Fleans.Application.Deployments;
using Fleans.Application.Effects;
using Fleans.Application.StartEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

            services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
            services.AddSingleton<ICompensationLogService, CompensationLogService>();
            services.AddSingleton<WorkflowDefinitionCache>();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<StartEventSubscriptionCache>();
            services.AddSingleton<StartEventLauncher>();

            services.AddSingleton<EffectDispatcher>();
            services.AddSingleton<IEffectHandler, TimerEffectHandler>();
            services.AddSingleton<IEffectHandler, MessageEffectHandler>();
            services.AddSingleton<IEffectHandler, SignalEffectHandler>();
            services.AddSingleton<IEffectHandler, UserTaskEffectHandler>();
            services.AddSingleton<IEffectHandler, WorkflowLifecycleEffectHandler>();
        }
    }
}
