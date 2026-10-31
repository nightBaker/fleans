using Fleans.Application.Deployments;
using Fleans.Application.Effects;
using Fleans.Application.ExternalTasks;
using Fleans.Application.Grains;
using Fleans.Application.Projections;
using Fleans.Application.StartEvents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
                .Validate(o => o.ScriptResultTimeout > TimeSpan.Zero, "Fleans:Worker:ScriptResultTimeout must be greater than 0.")
                .ValidateOnStart();

            services.AddOptions<SignalCorrelationOptions>()
                .BindConfiguration(SignalCorrelationOptions.SectionName)
                .Validate(o => o.BucketCount > 0, "Fleans:Signals:BucketCount must be greater than 0.")
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
            services.AddOptions<ProjectorOptions>()
                .BindConfiguration(ProjectorOptions.SectionName)
                .Validate(o => o.MaxBatchSize is >= 1 and <= ProjectorOptions.MaxBatchSizeLimit,
                    $"Fleans:Projector:MaxBatchSize must be between 1 and {ProjectorOptions.MaxBatchSizeLimit}.")
                .Validate(o => o.MaxBatchDelayMs >= 0, "Fleans:Projector:MaxBatchDelayMs can't be negative.")
                .Validate(o => o.MaxConcurrentBatches >= 1, "Fleans:Projector:MaxConcurrentBatches must be at least 1.")
                .ValidateOnStart();

            services.AddOptions<ExternalTaskOptions>()
                .BindConfiguration(ExternalTaskOptions.SectionName)
                .Validate(o => o.TaskTypes.Count == 0 || !string.IsNullOrWhiteSpace(o.Provider),
                    "Fleans:ExternalTasks:TaskTypes is set but Fleans:ExternalTasks:Provider is not.")
                .ValidateOnStart();

            services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
            services.AddSingleton<ICompensationLogService, CompensationLogService>();
            services.AddSingleton<WorkflowDefinitionCache>();
            services.AddSingleton<ProjectionBatcher>();
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
