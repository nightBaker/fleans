using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProjectTemplate.APPLICATION.Interfaces.Persistence;
using Fleans.Application.Interfaces.Persistence.CommandRepositories;
using Fleans.Persistence.Repositories.Commands;
using Fleans.Persistence;
using Fleans.Persistence.QueryServices;
using Fleans.Application.Interfaces.Persistence.QueryServices;

namespace ProjectTemplate.PERSISTENCE
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddDatabaseDeveloperPageExceptionFilter();

            services.AddDbContext<FleansDbContext>(options =>
                options.UseNpgsql(configuration.GetConnectionString("ProjectTemplateDbConnection")));

            services.AddScoped(typeof(ICommandRepository<>),typeof( CommandRepository<>));
            services.AddScoped(typeof(IQueryService<,>), typeof(QueryService<,>));
            services.AddScoped<IUnitOfWork, UnitOfWork>();

            return services;
        }
    }
}
