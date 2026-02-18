using Fleans.Application;
using Fleans.Application.Logging;
using Fleans.Infrastructure;
using Fleans.Persistence;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire components first
// This must be called before UseOrleans when running through Aspire
builder.AddServiceDefaults();

// Orleans silo configuration with Redis
// When running through Aspire, Redis connection string is automatically injected
builder.UseOrleans(siloBuilder =>
{
    // Configure Redis clustering
    // Aspire injects the Redis connection string via configuration
    siloBuilder.UseRedisClustering(options =>
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
    });

    // Configure Redis grain storage
    siloBuilder.AddRedisGrainStorage("PubSubStore", options =>
    {
        var connectionString = builder.Configuration.GetConnectionString("redis");
        if (!string.IsNullOrEmpty(connectionString))
        {
            // Parse the full connection string (includes password, SSL, etc.)
            options.ConfigurationOptions = ConfigurationOptions.Parse(connectionString);
            options.ConfigurationOptions.AbortOnConnectFail = false;
            
            // For development: accept untrusted SSL certificates
            // In production, use proper SSL certificates
            if (builder.Environment.IsDevelopment())
            {
                options.ConfigurationOptions.CertificateValidation += (sender, certificate, chain, errors) => true;
            }
        }
    });

    // Configure Redis streaming (optional, for pub/sub)
    siloBuilder.AddMemoryStreams("StreamProvider");

    // Reminders (required by TimerStartEventSchedulerGrain and WorkflowInstance timers)
    // TODO: Replace with persistent reminder storage (Redis/AzureTable) for production
    siloBuilder.UseInMemoryReminderService();

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

// EF Core persistence for ActivityInstanceState + WorkflowInstanceState
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

app.UseAuthorization();

app.MapControllers();

app.Run();
