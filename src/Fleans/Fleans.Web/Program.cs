using System.Net;
using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults;
using Fleans.Web.Components;
using Fleans.Web.Security;
using Fleans.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.StackExchangeRedis;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Orleans.Dashboard;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults first (includes service discovery for Aspire)
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add Fluent UI services
builder.Services.AddFluentUIComponents();
builder.Services.AddScoped<ThemeService>();

// Add Application and Infrastructure services
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// EF Core persistence — provider selected by Persistence:Provider config key (default: Sqlite)
builder.AddFleansPersistence();

// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client with Dashboard UI
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.AddDashboard();
});

// Authentication — opt-in: only enabled when both Authority and ClientId are configured.
// Single source of truth (D2a). Mirrors Fleans.Api's Authentication section name so the
// same Aspire parameter block can configure both services against the same IdP.
var authAuthority = builder.Configuration["Authentication:Authority"];
var authClientId = builder.Configuration["Authentication:ClientId"];
var authEnabled = !string.IsNullOrEmpty(authAuthority) && !string.IsNullOrEmpty(authClientId);

builder.Services.AddSingleton(new AuthOptions(authEnabled, authAuthority ?? "", authClientId ?? ""));

if (authEnabled)
{
    var cookieMinutes = builder.Configuration.GetValue("Authentication:CookieExpireMinutes", 60);

    builder.Services
        .AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(o =>
        {
            o.ExpireTimeSpan = TimeSpan.FromMinutes(cookieMinutes);
            o.SlidingExpiration = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        })
        .AddOpenIdConnect(o =>
        {
            o.Authority = authAuthority;
            o.ClientId = authClientId;
            o.ClientSecret = builder.Configuration["Authentication:ClientSecret"];
            o.ResponseType = "code";
            o.UsePkce = true;
            // Slice 3 has no API-token consumer; future slices flip together with refresh wiring (D13).
            o.SaveTokens = false;
            // Keycloak omits email/name from the ID token in default config — backfill from UserInfo.
            o.GetClaimsFromUserInfoEndpoint = true;
            o.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
            o.TokenValidationParameters.NameClaimType = "preferred_username";
            // Prepares the deferred role-policy slice — claim is harmless when absent.
            o.TokenValidationParameters.RoleClaimType = "roles";
        });

    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());

    // Multi-instance support: persist Data Protection keys to the keyed orleans-redis multiplexer
    // so cookies issued by replica A decrypt on replica B (D10). Configured via
    // KeyManagementOptions because PersistKeysToStackExchangeRedis lacks an
    // IServiceProvider-aware overload for keyed Redis resolution.
    builder.Services.AddDataProtection().SetApplicationName("Fleans.Web");
    builder.Services.AddOptions<KeyManagementOptions>()
        .Configure<IServiceProvider>((opts, sp) =>
        {
            var multiplexer = sp.GetRequiredKeyedService<IConnectionMultiplexer>("orleans-redis");
            opts.XmlRepository = new RedisXmlRepository(
                () => multiplexer.GetDatabase(),
                "fleans-web-dataprotection-keys");
        });
}

builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

await app.EnsureDatabaseSchemaAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

if (authEnabled)
{
    // D12 — Forwarded headers for reverse-proxy deployments. Empty allowlists = headers ignored
    // (avoids spoofing from untrusted networks). CIDR strings parsed by System.Net.IPNetwork
    // (the canonical .NET 8+ type; Microsoft.AspNetCore.HttpOverrides.IPNetwork is deprecated).
    var fwdOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };
    foreach (var ip in builder.Configuration.GetSection("Authentication:KnownProxies").Get<string[]>() ?? [])
    {
        fwdOptions.KnownProxies.Add(IPAddress.Parse(ip));
    }
    foreach (var net in builder.Configuration.GetSection("Authentication:KnownNetworks").Get<string[]>() ?? [])
    {
        fwdOptions.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(net));
    }
    app.UseForwardedHeaders(fwdOptions);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (authEnabled)
{
    // D6 — Login challenge (anonymous, externally reachable).
    app.MapGet("/Account/Login", (string? returnUrl, HttpContext ctx) =>
    {
        var safe = IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = safe },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }).AllowAnonymous();

    // D6 — Logout (antiforgery-protected POST per D8; the NavMenu form binds the token).
    app.MapPost("/Account/Logout", (HttpContext ctx) =>
        Results.SignOut(
            new AuthenticationProperties { RedirectUri = "/" },
            [CookieAuthenticationDefaults.AuthenticationScheme,
             OpenIdConnectDefaults.AuthenticationScheme]));

    // D4 — Orleans Dashboard does not honour [Authorize]; gate it explicitly before MapOrleansDashboard.
    app.UseWhen(
        ctx => ctx.Request.Path.StartsWithSegments("/dashboard"),
        branch => branch.Use(async (ctx, next) =>
        {
            if (ctx.User.Identity?.IsAuthenticated != true)
            {
                await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme);
                return;
            }
            await next();
        }));
}

// Orleans Dashboard at /dashboard
app.MapOrleansDashboard(routePrefix: "/dashboard");
app.MapDefaultEndpoints();

app.Run();

// D6 — local-URL predicate. Same shape as Microsoft.AspNetCore.Mvc.IUrlHelper.IsLocalUrl,
// expressed as a free static so the minimal-API delegate compiles without an IUrlHelper dependency.
// Rejects null/empty, paths without a leading '/', protocol-relative ("//evil.com"), and
// back-slash-escape ("/\evil.com") shapes. '\\' in source is one backslash character.
static bool IsLocalUrl(string? url) =>
    !string.IsNullOrEmpty(url)
    && url[0] == '/'
    && (url.Length == 1
        || (url[1] != '/' && url[1] != '\\'));
