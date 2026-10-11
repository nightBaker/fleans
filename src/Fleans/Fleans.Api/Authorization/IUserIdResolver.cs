namespace Fleans.Api.Authorization;

/// <summary>
/// Resolves the acting user id for user-task claim/complete (#793). Two implementations
/// are registered by <c>Program.cs</c>, mirroring <see cref="IUserGroupResolver"/>:
///
/// <list type="bullet">
///   <item>
///     <see cref="BodyUserIdResolver"/> — used when auth is disabled. Trusts the
///     body-supplied <c>UserId</c>.
///   </item>
///   <item>
///     <see cref="JwtUserIdResolver"/> — used when <c>Authentication:Authority</c> is
///     configured. Reads the configured JWT claim (default <c>preferred_username</c>).
///     A body-supplied <c>UserId</c> is optional; when present it must match the token,
///     otherwise the request is rejected so a caller can't act as another user.
///   </item>
/// </list>
/// </summary>
public interface IUserIdResolver
{
    UserIdResolution Resolve(HttpContext httpContext, string? bodyUserId);
}

public enum UserIdResolutionStatus
{
    /// <summary><see cref="UserIdResolution.UserId"/> is the acting user.</summary>
    Resolved,

    /// <summary>No user id available at all (auth disabled and body <c>UserId</c> empty).</summary>
    Missing,

    /// <summary>The authenticated principal carries no user-id claim.</summary>
    NoIdentityClaim,

    /// <summary>The body <c>UserId</c> differs from the authenticated principal.</summary>
    Mismatch,
}

public readonly record struct UserIdResolution(UserIdResolutionStatus Status, string? UserId = null)
{
    public static UserIdResolution Resolved(string userId) => new(UserIdResolutionStatus.Resolved, userId);
    public static readonly UserIdResolution Missing = new(UserIdResolutionStatus.Missing);
    public static readonly UserIdResolution NoIdentityClaim = new(UserIdResolutionStatus.NoIdentityClaim);
    public static readonly UserIdResolution Mismatch = new(UserIdResolutionStatus.Mismatch);
}
