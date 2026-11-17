using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleans.ServiceDefaults.Authorization;

/// <summary>
/// The Fleans roles (#720), as they appear in the caller's role claim. <see cref="Admin"/> ⊇
/// <see cref="Operator"/> ⊇ <see cref="User"/> is a hierarchy; <see cref="Deployer"/> and
/// <see cref="Debugger"/> each grant one capability on their own, and <see cref="Admin"/> includes both.
/// </summary>
public static class FleansRoles
{
    /// <summary>Deploys, disables, migrates and purges; approves deploy requests; edits settings.</summary>
    public const string Admin = "admin";

    /// <summary>Starts and inspects instances, sends messages/signals, runs external-task workers.</summary>
    public const string Operator = "operator";

    /// <summary>Tasklist only: lists, claims and completes user tasks.</summary>
    public const string User = "user";

    /// <summary>Deploys definitions (directly or by submitting a deploy request) and opens the editor.</summary>
    public const string Deployer = "deployer";

    /// <summary>Uses the Admin UI instance debugger (journal step-through). Combine with <see cref="Operator"/>.</summary>
    public const string Debugger = "debugger";
}

/// <summary>Authorization policy names applied to API endpoints and Admin UI pages.</summary>
public static class FleansPolicies
{
    public const string RequireAdmin = nameof(RequireAdmin);
    public const string RequireOperator = nameof(RequireOperator);
    public const string RequireUser = nameof(RequireUser);
    public const string RequireDeploy = nameof(RequireDeploy);
    public const string RequireDebug = nameof(RequireDebug);
}

public static class FleansAuthorizationExtensions
{
    /// <summary>Default role claim; override with <c>Authentication:RoleClaim</c>.</summary>
    public const string DefaultRoleClaim = "roles";

    /// <summary>
    /// Registers the <see cref="FleansPolicies"/>. With <paramref name="authEnabled"/> each policy
    /// requires an authenticated caller holding the role or one above it, read from the
    /// <c>Authentication:RoleClaim</c> claim. With auth disabled every policy allows everyone, so
    /// the dev-mode behaviour (no identity at all) stays unchanged.
    /// </summary>
    public static AuthorizationBuilder AddFleansRolePolicies(
        this IServiceCollection services, IConfiguration configuration, bool authEnabled)
    {
        var builder = services.AddAuthorizationBuilder();
        if (!authEnabled)
        {
            builder.AddPolicy(FleansPolicies.RequireAdmin, AllowAll);
            builder.AddPolicy(FleansPolicies.RequireOperator, AllowAll);
            builder.AddPolicy(FleansPolicies.RequireUser, AllowAll);
            builder.AddPolicy(FleansPolicies.RequireDeploy, AllowAll);
            builder.AddPolicy(FleansPolicies.RequireDebug, AllowAll);
            return builder;
        }

        var roleClaim = ResolveRoleClaim(configuration);
        builder.AddPolicy(FleansPolicies.RequireAdmin, Require(roleClaim, FleansRoles.Admin));
        builder.AddPolicy(FleansPolicies.RequireOperator, Require(roleClaim, FleansRoles.Admin, FleansRoles.Operator));
        builder.AddPolicy(FleansPolicies.RequireUser, Require(roleClaim, FleansRoles.Admin, FleansRoles.Operator, FleansRoles.User));
        builder.AddPolicy(FleansPolicies.RequireDeploy, Require(roleClaim, FleansRoles.Admin, FleansRoles.Deployer));
        builder.AddPolicy(FleansPolicies.RequireDebug, Require(roleClaim, FleansRoles.Admin, FleansRoles.Debugger));
        return builder;
    }

    /// <summary>
    /// The role claim name: <c>Authentication:RoleClaim</c>, or <see cref="DefaultRoleClaim"/> when blank.
    /// The one place that reads it, so the policies and the Admin UI's OIDC claim mapping agree.
    /// </summary>
    public static string ResolveRoleClaim(IConfiguration configuration)
    {
        var roleClaim = configuration["Authentication:RoleClaim"];
        return string.IsNullOrWhiteSpace(roleClaim) ? DefaultRoleClaim : roleClaim;
    }

    /// <summary>
    /// True when <paramref name="user"/> carries any of <paramref name="roles"/> in
    /// <paramref name="roleClaim"/>. Role values compare ordinally (case-sensitive), like group names.
    /// </summary>
    public static bool HasAnyRole(ClaimsPrincipal user, string roleClaim, params string[] roles)
    {
        // The JWT bearer and OIDC handlers' default inbound claim mapping renames `roles` / `role`
        // to ClaimTypes.Role, so accept that type too for those names (same idea as the `sub`
        // fallback in JwtUserIdResolver).
        var acceptMapped = roleClaim is "roles" or "role";
        return user.Claims.Any(c =>
            (c.Type == roleClaim || (acceptMapped && c.Type == ClaimTypes.Role))
            && roles.Contains(c.Value, StringComparer.Ordinal));
    }

    private static void AllowAll(AuthorizationPolicyBuilder policy) => policy.RequireAssertion(_ => true);

    private static Action<AuthorizationPolicyBuilder> Require(string roleClaim, params string[] roles) =>
        policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => HasAnyRole(ctx.User, roleClaim, roles));
}
