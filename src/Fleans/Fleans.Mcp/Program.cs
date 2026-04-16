using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

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
app.MapMcp();

app.Run();
