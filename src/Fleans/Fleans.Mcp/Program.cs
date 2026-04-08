using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Application + Infrastructure services (same as Web project)
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// EF Core persistence — provider selected by Persistence:Provider config key (default: Sqlite)
// Note: grain storage registrations from AddEfCorePersistence are unused in this
// Orleans client, but splitting the registration is a future refactor.
var persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "Sqlite";
if (persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var pgConnectionString = builder.Configuration.GetConnectionString("fleans")
        ?? throw new InvalidOperationException("Connection string 'fleans' is required when Persistence:Provider=Postgres");
    var pgQueryConnectionString = builder.Configuration.GetConnectionString("fleans-query");
    builder.Services.AddPostgresPersistence(pgConnectionString, pgQueryConnectionString);
}
else
{
    var sqliteConnectionString = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
    var queryConnectionString = builder.Configuration["FLEANS_QUERY_CONNECTION"];
    builder.Services.AddSqlitePersistence(sqliteConnectionString, queryConnectionString);
}

// Redis for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client
builder.UseOrleansClient();

// MCP server with Streamable HTTP transport
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Ensure EF Core database schema is ready on startup
if (!persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    // SQLite: use EnsureCreated — idempotent, race-safe for shared SQLite file with the Api silo
    using var scope = app.Services.CreateScope();
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
    using var db = dbFactory.CreateDbContext();
    SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(db.Database);
}

app.MapDefaultEndpoints();
app.MapMcp();

app.Run();
