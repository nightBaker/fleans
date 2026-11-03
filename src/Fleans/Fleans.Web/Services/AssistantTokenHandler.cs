using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;

namespace Fleans.Web.Services;

/// <summary>
/// Forwards the signed-in user's OIDC access token to the assistant backend (#1015) as
/// <c>Authorization: Bearer</c>. The token comes from the auth cookie (<c>SaveTokens</c> is on
/// while the assistant is configured) of the circuit's connection; Fleans does not refresh it, so
/// once it expires the backend answers 401 and the panel asks the user to sign in again. Without
/// authentication, or without a token, the request goes out without the header.
/// </summary>
public sealed class AssistantTokenHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext is { } context
            && await context.GetTokenAsync("access_token") is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}
