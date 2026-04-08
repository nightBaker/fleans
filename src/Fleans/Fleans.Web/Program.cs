using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.Web.Components;
using Fleans.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using Orleans.Dashboard;

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

// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client with Dashboard UI
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.AddDashboard();
});

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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Orleans Dashboard at /dashboard
app.MapOrleansDashboard(routePrefix: "/dashboard");
app.MapDefaultEndpoints();

app.Run();