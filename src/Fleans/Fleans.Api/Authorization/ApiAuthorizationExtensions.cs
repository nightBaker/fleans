using Fleans.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Fleans.Api.Authorization;

/// <summary>
/// API authorization wiring shared by <c>Program.cs</c> and the role-matrix tests (#720).
/// Controllers carry <c>[Authorize(Policy = FleansPolicies.*)]</c>; this registers those policies
/// and, when auth is on, a fallback that still requires an authenticated caller anywhere a policy
/// is not named. With auth off every policy allows everyone (dev mode).
/// </summary>
public static class ApiAuthorizationExtensions
{
    public static IServiceCollection AddFleansApiAuthorization(
        this IServiceCollection services, IConfiguration configuration, bool authEnabled)
    {
        var builder = services.AddFleansRolePolicies(configuration, authEnabled);
        if (authEnabled)
        {
            builder.SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());
        }
        return services;
    }
}
