using System.Threading.RateLimiting;
using Fleans.Api;
using Fleans.Api.Authorization;
using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Application.Placement;
using Fleans.Plugins.RestCaller;
using Fleans.Infrastructure;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Fleans.ServiceDefaults;
using Fleans.ServiceDefaults.Reminders;
using Fleans.Worker.Hosting;
using Fleans.Worker.Placement;
using Microsoft.AspNetCore.Authentication;
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

// Authentication — opt-in for production-grade JWT, but the auth *pipeline* is
// always registered. With Authentication:Authority configured we wire JwtBearer
// the usual way; without it (dev/staging only — Production is refused below)
// we register a synthetic "DevAnonymous" scheme that always succeeds. This
// keeps a single auth/authz pipeline across all environments so:
//   * controllers can add [Authorize] / [Authorize(Roles = …)] without breaking
//     the dev loop with "No authenticationScheme was specified";
//   * the fallback policy runs identically everywhere — a future change that
//     accidentally removes [Authorize] from a controller still gets caught;
//   * audit logs see a single named identity ("dev-anonymous") instead of an
//     empty principal.
//
// DevAnonymousAuthHandler is explicitly not a security control — it admits
// everyone. The security boundary is the Production fail-closed guard right
// below and the operator's network perimeter for staging.
var authAuthority = builder.Configuration["Authentication:Authority"];
var authEnabled = !string.IsNullOrEmpty(authAuthority);

// Fail-closed in Production: refuse to start if Authentication:Authority is unset.
// Without this, the else-branch below would silently register DevAnonymousAuthHandler
// in prod and admit every caller as "dev-anonymous". Operators who want an
// unauthenticated deployment must opt in by setting ASPNETCORE_ENVIRONMENT to
// Development or Staging.
if (!authEnabled && builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "Fleans.Api refuses to start in Production without authentication. " +
        "Set 'Authentication:Authority' (OIDC issuer URL) and 'Authentication:Audience'. " +
        "To run unauthenticated for local/dev use, set ASPNETCORE_ENVIRONMENT to " +
        "Development or Staging.");
}

if (authEnabled)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.Authority = authAuthority;
            opts.Audience = builder.Configuration["Authentication:Audience"] ?? "fleans-api";
            opts.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
        });
}
else
{
    builder.Services.AddAuthentication(DevAnonymousAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAnonymousAuthHandler>(
            DevAnonymousAuthHandler.SchemeName, configureOptions: null);
}

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// User-task group resolver (#588): JWT-derived when auth is enabled, body-derived
// otherwise. Mirrors the IUserTaskFilterStrategy precedent — chosen by config at
// startup; controller takes the interface.
if (authEnabled)
{
    builder.Services.AddSingleton<IUserGroupResolver, JwtUserGroupResolver>();
}
else
{
    builder.Services.AddSingleton<IUserGroupResolver, BodyUserGroupResolver>();
}

// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Fleans:Role — controls which grain set this silo hosts. Validated against the
// allowed set at startup so a typo fails fast rather than silently drifting into
// a silo that hosts no grains. The role is also stamped onto the silo name so
// other silos (and the Orleans dashboard) can see it via membership.
var roleRaw = builder.Configuration["Fleans:Role"] ?? "Combined";
var role = roleRaw.ToLowerInvariant();
if (role == "plugin")
{
    throw new InvalidOperationException(
        "Fleans.Api does not support Fleans:Role=Plugin. The 'Plugin' role is reserved " +
        "for external custom worker hosts (see docs/concepts/custom-tasks.md). Use Core, " +
        "Worker, or Combined for engine silos.");
}
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
    }

    // Fleans reminders: Redis-backed for BPMN timer durability across silo restarts (#650).
    // Fails fast at startup if 'orleans-redis' connection string is missing.
    siloBuilder.AddFleansReminders(builder.Configuration);

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

    // Fail fast on Fleans:Role / placement-attribute mismatch (#457).
    siloBuilder.AddFleansPlacementAssertion(builder.Configuration);
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
builder.Services.AddRestCallerPlugin();

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

// Auth pipeline is registered unconditionally (see the auth setup block above).
// In production with JWT this enforces real auth; in dev/staging without
// Authentication:Authority it runs the DevAnonymousAuthHandler which admits all
// callers under a single synthetic identity.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints();

app.Run();
