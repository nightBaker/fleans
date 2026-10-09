using System.Security.Claims;

namespace Fleans.Api.Authorization;

/// <summary>
/// Reads the acting user id from the caller's JWT bearer token (#793). Registered when
/// <c>Authentication:Authority</c> is set. Configure the claim via
/// <c>Authentication:UserIdClaim</c> (default <c>preferred_username</c>). The value must
/// match what BPMN <c>assignee</c> / <c>candidateUsers</c> expressions produce.
///
/// The body-supplied <c>UserId</c> is optional under JWT. When it is present and differs
/// (ordinal) from the token's value the request is a <see cref="UserIdResolutionStatus.Mismatch"/>
/// — the caller is trying to act as somebody else.
/// </summary>
public sealed class JwtUserIdResolver : IUserIdResolver
{
    private readonly string _claimName;

    public JwtUserIdResolver(IConfiguration configuration)
    {
        _claimName = configuration["Authentication:UserIdClaim"] ?? "preferred_username";
    }

    public UserIdResolution Resolve(HttpContext httpContext, string? bodyUserId)
    {
        var user = httpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
            return UserIdResolution.NoIdentityClaim;

        var tokenUserId = FindClaimValue(user);
        if (string.IsNullOrWhiteSpace(tokenUserId))
            return UserIdResolution.NoIdentityClaim;

        if (!string.IsNullOrWhiteSpace(bodyUserId) && !string.Equals(bodyUserId, tokenUserId, StringComparison.Ordinal))
            return UserIdResolution.Mismatch;

        return UserIdResolution.Resolved(tokenUserId);
    }

    private string? FindClaimValue(ClaimsPrincipal user)
    {
        var value = user.FindFirst(_claimName)?.Value;
        // JwtBearer's default inbound claim mapping renames `sub` to NameIdentifier.
        if (value is null && _claimName == "sub")
            value = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return value;
    }
}
