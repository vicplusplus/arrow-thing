using ArrowThing.Server.Coop;
using ArrowThing.Server.Data;
using ArrowThing.Server.Games;
using ArrowThing.Server.Leaderboards;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Serilog
var logConfig = new LoggerConfiguration().WriteTo.Console();

var lokiUrl = builder.Configuration["Loki:Url"];
if (!string.IsNullOrEmpty(lokiUrl))
{
    logConfig.WriteTo.GrafanaLoki(
        lokiUrl,
        labels: [new LokiLabel { Key = "app", Value = "arrow-thing-worker" }]
    );
}

Log.Logger = logConfig.CreateLogger();
builder.Services.AddSerilog();

// Database
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Redis
var redisConnectionString = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrEmpty(redisConnectionString))
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        ConnectionMultiplexer.Connect(redisConnectionString)
    );
}

builder.Services.AddSingleton<LeaderboardCache>(sp => new LeaderboardCache(
    sp.GetService<IConnectionMultiplexer>()
));

// Co-op generation worker dependencies
builder.Services.AddSingleton<AccountConcurrencyLimiter>();
builder.Services.AddSingleton<GenerationProgressBus>();
builder.Services.AddScoped<LobbySnapshotRepository>();

builder.Services.AddHostedService<VerificationWorker>();
builder.Services.AddHostedService<LobbyGenerationWorker>();

var host = builder.Build();

// Ensure DB is migrated
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

await host.RunAsync();
