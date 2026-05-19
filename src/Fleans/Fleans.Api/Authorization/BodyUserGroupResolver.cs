using Fleans.ServiceDefaults.DTOs;

namespace Fleans.Api.Authorization;

/// <summary>
/// Reads <see cref="ClaimTaskRequest.UserGroups"/> from the request body verbatim.
/// Registered when JWT authentication is NOT configured (the default Fleans
/// deployment posture). Trusted on the wire — operators deploying with auth disabled
/// must accept that claim-time spoofing is possible by definition.
/// </summary>
public sealed class BodyUserGroupResolver : IUserGroupResolver
{
    public IReadOnlyList<string> Resolve(HttpContext httpContext, ClaimTaskRequest request)
        => request.UserGroups ?? Array.Empty<string>();
}
