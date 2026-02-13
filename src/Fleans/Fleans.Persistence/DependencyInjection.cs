using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Storage;

namespace Fleans.Persistence;

public static class EfCorePersistenceDependencyInjection
{
    public static void AddEfCorePersistence(this IServiceCollection services, Action<DbContextOptionsBuilder> configureDb)
    {
        services.AddDbContextFactory<GrainStateDbContext>(configureDb);

        services.AddKeyedSingleton<IGrainStorage>("activityInstances",
            (sp, _) => new EfCoreActivityInstanceGrainStorage(
                sp.GetRequiredService<IDbContextFactory<GrainStateDbContext>>()));
    }
}
