using Fleans.Application;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults;
using Fleans.Web.Components;
using Fleans.Web.Services;
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
builder.AddFleansPersistence();

// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans client with Dashboard UI
builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.AddDashboard();
});

var app = builder.Build();

await app.EnsureDatabaseSchemaAsync();

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