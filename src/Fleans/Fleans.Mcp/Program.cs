using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Application + Infrastructure services (same as Web project)
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// EF Core persistence — shared SQLite file with Api silo.
// Note: grain storage registrations from AddEfCorePersistence are unused in this
// Orleans client, but splitting the registration is a future refactor.
var sqliteConnectionString = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
builder.Services.AddEfCorePersistence(options => options.UseSqlite(sqliteConnectionString));

// Redis for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client
builder.UseOrleansClient();

// MCP server with Streamable HTTP transport
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Ensure EF Core database exists (dev only — use migrations in production).
using (var scope = app.Services.CreateScope())
{
    EfCorePersistenceDependencyInjection.EnsureDatabaseCreated(scope.ServiceProvider);
}

app.MapDefaultEndpoints();
app.MapMcp();

app.Run();
