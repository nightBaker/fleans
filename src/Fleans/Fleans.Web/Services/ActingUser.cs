using Fleans.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fleans.Web.Services;

/// <summary>
/// Who is requesting or reviewing a deployment (#900), per circuit. With authentication on it is
/// the signed-in user (<c>preferred_username</c>); without, a name the user declares once per
/// session — the same trust model as the API's body <c>UserId</c>.
/// </summary>
public sealed class ActingUser(AuthenticationStateProvider authenticationState, AuthOptions authOptions)
{
    /// <summary>True when the name comes from the sign-in; false when the user types it.</summary>
    public bool IsSignedIn => authOptions.Enabled;

    /// <summary>The self-declared name used when authentication is off.</summary>
    public string? DeclaredName { get; set; }

    public async Task<string?> GetUserIdAsync()
    {
        if (!authOptions.Enabled)
            return string.IsNullOrWhiteSpace(DeclaredName) ? null : DeclaredName.Trim();

        var state = await authenticationState.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true ? state.User.Identity.Name : null;
    }
}
