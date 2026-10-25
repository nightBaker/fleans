using Fleans.Application.Deployments;
using Fleans.Application.Effects;
using Fleans.Application.Projections;
using Microsoft.Extensions.DependencyInjection;

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

            services.AddOptions<ProjectorOptions>()
                .BindConfiguration(ProjectorOptions.SectionName)
                .Validate(o => o.MaxBatchSize is >= 1 and <= ProjectorOptions.MaxBatchSizeLimit,
                    $"Fleans:Projector:MaxBatchSize must be between 1 and {ProjectorOptions.MaxBatchSizeLimit}.")
                .Validate(o => o.MaxBatchDelayMs >= 0, "Fleans:Projector:MaxBatchDelayMs can't be negative.")
                .Validate(o => o.MaxConcurrentBatches >= 1, "Fleans:Projector:MaxConcurrentBatches must be at least 1.")
                .ValidateOnStart();

            services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
            services.AddSingleton<ICompensationLogService, CompensationLogService>();
            services.AddSingleton<WorkflowDefinitionCache>();
            services.AddSingleton<ProjectionBatcher>();

            services.AddSingleton<EffectDispatcher>();
            services.AddSingleton<IEffectHandler, TimerEffectHandler>();
            services.AddSingleton<IEffectHandler, MessageEffectHandler>();
            services.AddSingleton<IEffectHandler, SignalEffectHandler>();
            services.AddSingleton<IEffectHandler, UserTaskEffectHandler>();
            services.AddSingleton<IEffectHandler, WorkflowLifecycleEffectHandler>();
        }
    }
}
