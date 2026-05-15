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
// Mirrors the pattern in Fleans.Api/Program.cs; default audience is "fleans-mcp" so
// MCP access can be revoked independently from the REST API.
var authAuthority = builder.Configuration["Authentication:Authority"];
var authEnabled = !string.IsNullOrEmpty(authAuthority);
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

app.MapDefaultEndpoints();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapMcp();

app.Run();
