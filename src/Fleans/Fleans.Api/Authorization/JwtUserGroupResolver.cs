using Fleans.ServiceDefaults.DTOs;

namespace Fleans.Api.Authorization;

/// <summary>
/// Reads group membership from the calling user's JWT bearer token. Registered when
/// <c>Authentication:Authority</c> is set. Body-supplied <c>UserGroups</c> is
/// IGNORED — an authenticated caller cannot spoof groups they don't have.
///
/// JWT claim shape is the OIDC repeated-claim convention: multiple claim entries
/// with the same name, one group per entry. Configure the claim name via
/// <c>Authentication:GroupsClaim</c> (default <c>groups</c>). JSON-array-encoded
/// single-claim tokens are NOT auto-parsed; operators with array-encoded IdPs must
/// either configure the IdP to repeat the claim or wait for the follow-up issue
/// tracking JSON-array support.
/// </summary>
public sealed class JwtUserGroupResolver : IUserGroupResolver
{
    private readonly string _claimName;

    public JwtUserGroupResolver(IConfiguration configuration)
    {
        _claimName = configuration["Authentication:GroupsClaim"] ?? "groups";
    }

    public IReadOnlyList<string> Resolve(HttpContext httpContext, ClaimTaskRequest request)
    {
        // Body-supplied UserGroups is ignored when auth is enabled — the bearer token
        // is the authoritative source. Unauthenticated callers reach this branch only
        // if the controller's [Authorize] fallback policy is bypassed; defend with an
        // empty list rather than a throw so the domain layer's authorization check
        // produces the canonical rejection message.
        if (httpContext.User?.Identity?.IsAuthenticated != true)
            return Array.Empty<string>();

        return httpContext.User.FindAll(_claimName)
            .Select(c => c.Value)
            .ToList();
    }
}
