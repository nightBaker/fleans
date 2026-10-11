namespace Fleans.Api.Authorization;

/// <summary>
/// Trusts the body-supplied <c>UserId</c>. Registered when JWT authentication is NOT
/// configured — operators deploying with auth disabled accept that any caller can act
/// as any user by definition.
/// </summary>
public sealed class BodyUserIdResolver : IUserIdResolver
{
    public UserIdResolution Resolve(HttpContext httpContext, string? bodyUserId)
        => string.IsNullOrWhiteSpace(bodyUserId)
            ? UserIdResolution.Missing
            : UserIdResolution.Resolved(bodyUserId);
}
