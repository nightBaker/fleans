using Fleans.ServiceDefaults.DTOs;

namespace Fleans.Api.Authorization;

/// <summary>
/// Reads the body's <c>UserGroups</c> (e.g. <see cref="ClaimTaskRequest.UserGroups"/>) verbatim.
/// Registered when JWT authentication is NOT configured (the default Fleans
/// deployment posture). Trusted on the wire — operators deploying with auth disabled
/// must accept that claim-time spoofing is possible by definition.
/// </summary>
public sealed class BodyUserGroupResolver : IUserGroupResolver
{
    public IReadOnlyList<string> Resolve(HttpContext httpContext, IReadOnlyList<string>? bodyUserGroups)
        => bodyUserGroups ?? Array.Empty<string>();
}
