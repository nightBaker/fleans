using System.Threading.RateLimiting;
using Fleans.Api;
using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Orleans.Dashboard;

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
    // Dashboard data collection (UI served from Web project)
    siloBuilder.AddDashboard();

    // Structured workflow logging via RequestContext
    siloBuilder.AddIncomingGrainCallFilter<WorkflowLoggingScopeFilter>();
});

// Add services to the container.
builder.Services.AddProblemDetails();

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
            context.HttpContext.Response.ContentType = "application/json";
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

// EF Core persistence for WorkflowInstanceState
var sqliteConnectionString = builder.Configuration["FLEANS_SQLITE_CONNECTION"] ?? "DataSource=fleans-dev.db";
builder.Services.AddEfCorePersistence(options => options.UseSqlite(sqliteConnectionString));

var app = builder.Build();

// Ensure EF Core database is created (dev only — use migrations in production)
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FleanCommandDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

if (rateLimitConfig is not null)
    app.UseRateLimiter();

app.UseAuthorization();

app.MapControllers();
app.MapDefaultEndpoints();

app.Run();
