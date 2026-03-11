using Microsoft.Extensions.DependencyInjection;

namespace Fleans.Application
{
    public static class ApplicationDependencyInjection
    {
        public static void AddApplication(this IServiceCollection services)
        {
            services.AddSingleton<IWorkflowCommandService, WorkflowCommandService>();
        }
    }
}
