using Fleans.ServiceDefaults.DTOs;

namespace Fleans.Api.Authorization;

/// <summary>
/// Resolves the calling user's group membership for a user-task claim attempt. Two
/// implementations are registered by <c>Program.cs</c> based on whether JWT
/// authentication is configured (#588 round-2 design, minor #1 option (b')):
///
/// <list type="bullet">
///   <item>
///     <see cref="BodyUserGroupResolver"/> — used when auth is disabled. Reads
///     <see cref="ClaimTaskRequest.UserGroups"/> from the request body. Trusted on
///     the wire; appropriate for the default no-auth Fleans deployment.
///   </item>
///   <item>
///     <see cref="JwtUserGroupResolver"/> — used when <c>Authentication:Authority</c>
///     is configured. Reads the configured JWT claim (default <c>groups</c>) via
///     <see cref="System.Security.Claims.ClaimsPrincipal.FindAll"/>. Body-supplied
///     <c>UserGroups</c> is ignored to prevent caller-side spoofing.
///   </item>
/// </list>
///
/// Mirrors the <c>IUserTaskFilterStrategy</c> two-impl-by-config pattern at
/// <c>Fleans.Persistence/IUserTaskFilterStrategy.cs</c>.
/// </summary>
public interface IUserGroupResolver
{
    /// <summary>
    /// Returns the groups for the calling user. Returns an empty list when no groups
    /// are available; the domain authorization check at
    /// <c>UserTaskLifecycle.Claim</c> treats an empty list as "no group constraint
    /// satisfied".
    /// </summary>
    IReadOnlyList<string> Resolve(HttpContext httpContext, ClaimTaskRequest request);
}
