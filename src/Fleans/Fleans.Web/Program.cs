using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Fleans.Web.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults first (includes service discovery for Aspire)
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add Fluent UI services
builder.Services.AddFluentUIComponents();

// Add Application and Infrastructure services
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// EF Core persistence — shared SQLite file with Api silo
var sqliteConnectionString = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
builder.Services.AddEfCorePersistence(options => options.UseSqlite(sqliteConnectionString));

// Add Orleans client
// When running through Aspire, service discovery automatically provides gateway endpoints
// The Orleans client will connect to the Redis-clustered Orleans silo
builder.Host.UseOrleansClient(siloBuilder=> siloBuilder.UseRedisClustering(options =>
{
    // Aspire automatically provides the Redis connection string
    // when .WithReference(redis) is used in Aspire
    var connectionString = builder.Configuration.GetConnectionString("redis");
    if (!string.IsNullOrEmpty(connectionString))
    {
        // Parse the connection string to extract host:port and other parameters
        options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString);
        options.ConfigurationOptions.AbortOnConnectFail = false;
            
        // For development: accept untrusted SSL certificates
        // In production, use proper SSL certificates
        if (builder.Environment.IsDevelopment())
        {
            options.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) => true;
        }
    }
}));

var app = builder.Build();

// Ensure EF Core database is created (dev only — use migrations in production)
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();
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

app.Run();