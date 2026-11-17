using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace Fleans.Web.Security;

/// <summary>
/// One policy check for a page (#720). Pages evaluate the admin policy once into a field instead of an
/// <c>AuthorizeView</c> per control or per grid row. Blazor Server only runs event handlers for controls
/// it rendered, so not rendering a control is the gate. With auth off every policy allows everyone.
/// </summary>
public static class PolicyCheck
{
    public static async Task<bool> IsAllowedAsync(
        this IAuthorizationService authorization, Task<AuthenticationState>? authenticationState, string policy)
    {
        if (authenticationState is null)
            return false;
        var state = await authenticationState;
        return (await authorization.AuthorizeAsync(state.User, policy)).Succeeded;
    }
}
