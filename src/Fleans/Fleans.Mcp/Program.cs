using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Authentication — opt-in: only enabled when Authentication:Authority is configured.
// Mirrors the Fleans.Api convention so the same Aspire / Helm parameter block can
// configure both services against the same IdP. The `Audience` defaults to
// "fleans-mcp" so a single IdP issues distinct audiences for the REST API and the
// MCP server (operators can override via Authentication:Audience).
var authAuthority = builder.Configuration["Authentication:Authority"];
var authEnabled = !string.IsNullOrEmpty(authAuthority);

// Fail-closed in Production: the MCP server exposes DeployWorkflow and other
// engine-mutating tools over HTTP. With auth off, anyone who reaches the listener
// can upload BPMN and start workflows — and BPMN script tasks run server-side
// (DynamicExpresso). Refuse to start in Production rather than ship an open
// execution surface. Operators who knowingly want an unauthenticated deployment
// must set ASPNETCORE_ENVIRONMENT to Development or Staging.
if (!authEnabled && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "Fleans.Mcp refuses to start in Production without authentication. " +
        "Set 'Authentication:Authority' (OIDC issuer URL) and optionally " +
        "'Authentication:Audience' (default 'fleans-mcp'). " +
        "To run unauthenticated for local/dev use, set ASPNETCORE_ENVIRONMENT to " +
        "Development or Staging.");
}

if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.Authority = authAuthority;
            opts.Audience = builder.Configuration["Authentication:Audience"] ?? "fleans-mcp";
            opts.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
        });
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}

// Application + Infrastructure services (same as Web project)
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// EF Core persistence — provider selected by Persistence:Provider config key (default: Sqlite)
// Note: grain storage registrations from AddEfCorePersistence are unused in this
// Orleans client, but splitting the registration is a future refactor.
builder.AddFleansPersistence();

// Redis for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client
builder.UseOrleansClient();

// MCP server with Streamable HTTP transport
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

await app.EnsureDatabaseSchemaAsync();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else if (app.Environment.IsDevelopment())
{
    // Development convenience: keep the MCP listener available for local LLM
    // clients (Claude Code / Cursor / Continue) hitting http://localhost. Log a
    // loud warning so an operator who accidentally points a Development build
    // at a multi-tenant cluster notices what they just shipped. Staging and
    // Production with auth off are refused at startup or fall through with no
    // auth wiring respectively.
    app.Logger.LogWarning(
        "Fleans.Mcp is exposing tools over HTTP WITHOUT authentication " +
        "(Development environment, auth disabled). DeployWorkflow accepts " +
        "arbitrary BPMN — do not expose this listener to untrusted networks.");
}

app.MapDefaultEndpoints();

var mcpEndpoint = app.MapMcp();
if (authEnabled)
{
    // RequireAuthorization() attaches the fallback policy explicitly. Without
    // this, the MCP endpoint group is invisible to the authorization fallback
    // (the endpoint is not produced via MapControllers / MapRazorComponents),
    // so the policy would not run and tools would stay anonymous.
    mcpEndpoint.RequireAuthorization();
}

app.Run();
