using System.Threading.RateLimiting;
using Fleans.Api;
using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults;
using Fleans.Worker.Placement;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Dashboard;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime.Placement;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components first
// This must be called before UseOrleans when running through Aspire
builder.AddServiceDefaults();

// Authentication — opt-in: only enabled when Authentication:Authority is configured
var authAuthority = builder.Configuration["Authentication:Authority"];
var authEnabled = !string.IsNullOrEmpty(authAuthority);
if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.Authority = authAuthority;
            opts.Audience = builder.Configuration["Authentication:Audience"] ?? "fleans-api";
            opts.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
        });
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}

// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Fleans:Role — controls which grain set this silo hosts. Validated against the
// allowed set at startup so a typo fails fast rather than silently drifting into
// a silo that hosts no grains. The role is also stamped onto the silo name so
// other silos (and the Orleans dashboard) can see it via membership.
var roleRaw = builder.Configuration["Fleans:Role"] ?? "Combined";
var role = roleRaw.ToLowerInvariant();
if (role != "core" && role != "worker" && role != "combined")
{
    throw new InvalidOperationException(
        $"Fleans:Role must be one of 'Core', 'Worker', 'Combined' (case-insensitive) — got '{roleRaw}'.");
}
var siloName = $"{role}-{Environment.MachineName}-{Guid.NewGuid():N}".ToLowerInvariant();

// Orleans silo configuration
// Infrastructure (clustering, storage, streaming, reminders) is managed by Aspire AppHost.
// When running outside Aspire (e.g., Docker Compose load testing), FLEANS_LOAD_TEST_MODE=true
// activates explicit Redis clustering wiring. Aspire never sets this variable — safe as a guard.
var orleansRedisConnection = builder.Configuration.GetConnectionString("orleans-redis");
var isLoadTestMode = builder.Configuration["FLEANS_LOAD_TEST_MODE"] == "true";

builder.UseOrleans(siloBuilder =>
{
    // Stamp the role into the silo name so membership gossip exposes it cluster-wide.
    siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

    if (isLoadTestMode && !string.IsNullOrEmpty(orleansRedisConnection))
    {
        // Non-Aspire startup path: wire Redis clustering, PubSubStore, and reminders explicitly.
        siloBuilder.UseRedisClustering(orleansRedisConnection);
        siloBuilder.AddRedisGrainStorage("PubSubStore",
            options => options.ConfigurationOptions =
                ConfigurationOptions.Parse(orleansRedisConnection));
        siloBuilder.UseInMemoryReminderService();
    }

    // Pluggable stream provider — reads Fleans:Streaming:Provider from config (default: memory)
    siloBuilder.AddFleanStreaming(builder.Configuration);

    // Dashboard data collection (UI served from Web project)
    siloBuilder.AddDashboard();

    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();

    // JournaledGrain event sourcing: use CustomStorage backed by EfCoreEventStore
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();

    // Role-aware placement directors. Every silo registers both so a Core-only
    // silo can still route worker grains to its Worker siblings (and vice versa).
    // The director's fallback keeps test silos and Combined silos working even
    // when only a single silo exists.
    siloBuilder.AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>();
    siloBuilder.AddPlacementDirector<WorkerPlacementStrategy, WorkerPlacementDirector>();
});

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddApplication();
builder.Services.AddInfrastructure();

// Rate limiting — opt-in: only enabled when RateLimiting section is configured
var rateLimitSection = builder.Configuration.GetSection("RateLimiting");
var rateLimitConfig = rateLimitSection.Exists()
    ? rateLimitSection.Get<RateLimitingConfiguration>()
    : null;

if (rateLimitConfig is not null)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        AddPolicyIfConfigured(options, "workflow-mutation", rateLimitConfig.WorkflowMutation);
        AddPolicyIfConfigured(options, "task-operation", rateLimitConfig.TaskOperation);
        AddPolicyIfConfigured(options, "read", rateLimitConfig.Read);
        AddPolicyIfConfigured(options, "admin", rateLimitConfig.Admin);
        AddPolicyIfConfigured(options, "polling", rateLimitConfig.Polling);

        options.OnRejected = async (context, token) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterValue))
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfterValue.TotalSeconds).ToString();
            await context.HttpContext.Response.WriteAsJsonAsync(
                new { error = "Too many requests. Please retry later." }, token);
        };
    });
}

static void AddPolicyIfConfigured(RateLimiterOptions options, string policyName, RateLimitPolicy? policy)
{
    if (policy is null) return;
    options.AddPolicy(policyName, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromSeconds(policy.Window),
                PermitLimit = policy.PermitLimit,
                QueueLimit = 0
            }));
}

// EF Core persistence — provider selected by Persistence:Provider config key (default: Sqlite)
builder.AddFleansPersistence();

var app = builder.Build();

await app.EnsureDatabaseSchemaAsync();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseExceptionHandler();

if (rateLimitConfig is not null)
    app.UseRateLimiter();

if (authEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapControllers();
app.MapDefaultEndpoints();

app.Run();
