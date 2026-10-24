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
using Fleans.ServiceDefaults.Clustering;
using Fleans.ServiceDefaults.Reminders;
using Fleans.Worker.Hosting;
using Fleans.Worker.Placement;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Dashboard;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime.Placement;

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

// User-task group (#588) and user-id (#793) resolvers: JWT-derived when auth is enabled, body-derived
// otherwise. Mirrors the IUserTaskFilterStrategy precedent — chosen by config at
// startup; controller takes the interface.
if (authEnabled)
{
    builder.Services.AddSingleton<IUserGroupResolver, JwtUserGroupResolver>();
    builder.Services.AddSingleton<IUserIdResolver, JwtUserIdResolver>();
}
else
{
    builder.Services.AddSingleton<IUserGroupResolver, BodyUserGroupResolver>();
    builder.Services.AddSingleton<IUserIdResolver, BodyUserIdResolver>();
}

// Fleans:Role — controls which grain set this silo hosts. Validated against the
// allowed set at startup so a typo fails fast rather than silently drifting into
// a silo that hosts no grains. The role is also stamped onto the silo name so
// other silos (and the Orleans dashboard) can see it via membership.
// Api is not a silo: this host then runs as an Orleans client with no grains (REST only).
var roleRaw = builder.Configuration["Fleans:Role"] ?? "Combined";
var role = roleRaw.ToLowerInvariant();
if (role == "plugin")
{
    throw new InvalidOperationException(
        "Fleans.Api does not support Fleans:Role=Plugin. The 'Plugin' role is reserved " +
        "for external custom worker hosts (see docs/concepts/custom-tasks.md). Use Core, " +
        "Worker, or Combined for engine silos, or Api for a REST-only Orleans client.");
}
if (role != "core" && role != "worker" && role != "combined" && role != "api" && role != "projector")
{
    throw new InvalidOperationException(
        $"Fleans:Role must be one of 'Core', 'Worker', 'Combined', 'Api', 'Projector' (case-insensitive) — got '{roleRaw}'.");
}
var isSilo = role != "api";

// The orleans-redis client (and its /health check) only when a Redis-backed provider is configured.
builder.AddFleansRedisClient(isSilo ? FleansHostKind.Silo : FleansHostKind.Client);

if (!isSilo)
{
    // Orleans client, wired like Fleans.Web / Fleans.Mcp: the gateway list comes from the silos'
    // membership table (Fleans:Clustering:Provider); under Aspire, Orleans' own config wires it.
    // No stream provider: the REST endpoints only call grains and read the query database.
    builder.UseOrleansClient(clientBuilder => clientBuilder.AddFleansClustering(builder.Configuration));
}
else
{
    // With Fleans:Kubernetes:Enabled the silo name is the pod name (which must then start with the role).
    var siloName = FleansClusteringExtensions.ResolveSiloName(builder.Configuration, role);

    // Orleans silo configuration
    // Under Aspire the AppHost injects clustering through Orleans' own configuration.
    // Elsewhere AddFleansClustering wires the membership table from Fleans:Clustering:Provider (#885).
    // Streams use implicit subscriptions only, so no PubSubStore is registered (#911).
    builder.UseOrleans(siloBuilder =>
    {
        // Stamp the role into the silo name so membership gossip exposes it cluster-wide.
        siloBuilder.Configure<Orleans.Configuration.SiloOptions>(o => o.SiloName = siloName);

        // Membership table (Redis default | Postgres) + opt-in Kubernetes hosting. Fails fast on bad config.
        siloBuilder.AddFleansClustering(builder.Configuration);

        // Fleans reminders: Redis-backed for BPMN timer durability across silo restarts (#650).
        // Fails fast at startup if 'orleans-redis' connection string is missing.
        siloBuilder.AddFleansReminders(builder.Configuration);

        // Pluggable stream provider — reads Fleans:Streaming:Provider from config (default: redis)
        siloBuilder.AddFleanStreaming(builder.Configuration);

        // Opt-in Orleans load shedding at a silo CPU threshold (Fleans:LoadShedding).
        siloBuilder.AddFleansLoadShedding(builder.Configuration);

        // Dashboard data collection (UI served from Web project)
        siloBuilder.AddDashboard();

        // Structured workflow logging via RequestContext
        siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();

        // JournaledGrain event sourcing: use CustomStorage backed by EfCoreEventStore
        siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();

        // Role-aware placement: the [CorePlacement] director and the [WorkerPlacement] filter.
        // Every silo registers both so a Core-only silo can still route worker grains to its
        // Worker siblings (and vice versa). Their fallback keeps test silos and Combined silos
        // working even when only a single silo exists.
        siloBuilder.AddPlacementDirector<CorePlacementStrategy, CorePlacementDirector>();
        siloBuilder.AddPlacementDirector<ProjectorPlacementStrategy, ProjectorPlacementDirector>();
        siloBuilder.AddWorkerPlacementFilter();

        // A Projector silo (the fleans-projector deployment, #861) hosts the read-model projector
        // and no other Fleans grain; the projector prefers it over core silos.
        if (role == "projector")
            siloBuilder.Services.PostConfigure<Orleans.Configuration.GrainTypeOptions>(
                Fleans.Application.Projections.ProjectorSilo.KeepOnlyProjectorGrains);

        // Fail fast on Fleans:Role / placement-attribute mismatch (#457).
        siloBuilder.AddFleansPlacementAssertion(builder.Configuration);
    });

    // Engine-bundled custom-task plugin: its handler grain is hosted by silos only — and not by a
    // Projector silo, which hosts no plugin handler and must not advertise one in the catalog.
    if (role != "projector")
        builder.Services.AddRestCallerPlugin();
}

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
