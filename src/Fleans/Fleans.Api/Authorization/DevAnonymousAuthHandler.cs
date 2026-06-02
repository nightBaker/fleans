using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleans.Api.Authorization;

/// <summary>
/// Always-succeed authentication handler used in non-Production environments when
/// <c>Authentication:Authority</c> is not configured. Issues a synthetic
/// <see cref="ClaimsPrincipal"/> with subject <c>dev-anonymous</c> so the rest of
/// the pipeline (fallback authorization policy, <c>[Authorize]</c> attributes,
/// audit logging) sees a single, named identity instead of branching on
/// "auth enabled vs not enabled" everywhere.
///
/// This is NOT a security control — it allows everyone through. Production
/// deployments refuse to start with auth disabled (see <c>Fleans.Api/Program.cs</c>),
/// so this handler is unreachable in Production. The intent is to keep the
/// authentication pipeline structurally identical across dev / staging / prod
/// so that adding a future <c>[Authorize(Policy = …)]</c> in a controller doesn't
/// silently no-op in dev or crash when no scheme is registered.
/// </summary>
public sealed class DevAnonymousAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevAnonymous";
    public const string AnonymousSubject = "dev-anonymous";

    public DevAnonymousAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, AnonymousSubject),
                new Claim(ClaimTypes.Name, AnonymousSubject),
            ],
            authenticationType: SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
