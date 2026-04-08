using System.Threading.RateLimiting;
using Fleans.Api;
using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Fleans.Persistence.PostgreSql;
using Fleans.Persistence.Sqlite;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Orleans.Dashboard;
using Orleans.EventSourcing.CustomStorage;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components first
// This must be called before UseOrleans when running through Aspire
builder.AddServiceDefaults();

// Register Redis client for Aspire-managed Orleans
builder.AddKeyedRedisClient("orleans-redis");

// Orleans silo configuration
// Infrastructure (clustering, storage, streaming, reminders) is managed by Aspire AppHost
builder.UseOrleans(siloBuilder =>
{
    // Pluggable stream provider — reads Fleans:Streaming:Provider from config (default: memory)
    siloBuilder.AddFleanStreaming(builder.Configuration);

    // Dashboard data collection (UI served from Web project)
    siloBuilder.AddDashboard();

    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();

    // JournaledGrain event sourcing: use CustomStorage backed by EfCoreEventStore
    siloBuilder.AddCustomStorageBasedLogConsistencyProviderAsDefault();
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

var app = builder.Build();

// Ensure EF Core database is created / migrated on startup
using (var scope = app.Services.CreateScope())
{
    if (!persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        // SQLite: use EnsureCreated (fast, idempotent, no migration history required for dev)
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        using var db = dbFactory.CreateDbContext();
        SqliteSchemaInitializer.EnsureCreatedIgnoreRaces(db.Database);
    }
    else
    {
        // PostgreSQL: apply migrations (creates tables on first run, no-op on subsequent runs)
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
        await using var commandDb = dbFactory.CreateDbContext();
        await commandDb.Database.MigrateAsync();

        var queryFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanQueryDbContext>>();
        await using var queryDb = queryFactory.CreateDbContext();
        await queryDb.Database.MigrateAsync();
    }
}

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

app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints();

app.Run();
