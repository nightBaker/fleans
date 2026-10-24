using Fleans.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fleans.Web.Services;

/// <summary>
/// Who is requesting or reviewing a deployment (#900), per circuit. With authentication on it is
/// the signed-in user (<c>preferred_username</c>) and the groups in its
/// <c>Authentication:GroupsClaim</c> claims (default <c>groups</c>, as in the API); without, a
/// name and groups the user declares once per session — the same trust model as the API's body
/// <c>UserId</c> / <c>UserGroups</c>.
/// </summary>
public sealed class ActingUser(
    AuthenticationStateProvider authenticationState, AuthOptions authOptions, IConfiguration configuration)
{
    private readonly string _groupsClaim = configuration["Authentication:GroupsClaim"] ?? "groups";

    /// <summary>True when the name comes from the sign-in; false when the user types it.</summary>
    public bool IsSignedIn => authOptions.Enabled;

    /// <summary>The self-declared name used when authentication is off.</summary>
    public string? DeclaredName { get; set; }

    /// <summary>The self-declared groups, comma-separated, used when authentication is off.</summary>
    public string? DeclaredGroups { get; set; }

    public async Task<string?> GetUserIdAsync()
    {
        if (!authOptions.Enabled)
            return string.IsNullOrWhiteSpace(DeclaredName) ? null : DeclaredName.Trim();

        var state = await authenticationState.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true ? state.User.Identity.Name : null;
    }

    /// <summary><see cref="DeclaredGroups"/> as a list.</summary>
    public IReadOnlyList<string> DeclaredGroupList =>
        (DeclaredGroups ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<IReadOnlyList<string>> GetGroupsAsync()
    {
        if (!authOptions.Enabled)
            return DeclaredGroupList;

        var state = await authenticationState.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true
            ? state.User.FindAll(_groupsClaim).Select(c => c.Value).ToList()
            : [];
    }
}
