using System.Security.Claims;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Games;
using ArrowThing.Server.Leaderboards;
using ArrowThing.Server.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using StackExchange.Redis;

static bool VerifyAdminKey(IConfiguration config, HttpContext ctx)
{
    var adminKey = config["Admin:ApiKey"];
    if (string.IsNullOrEmpty(adminKey))
        return false;

    var provided = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? "";
    return PasswordHasher.FixedTimeEquals(provided, adminKey);
}

var builder = WebApplication.CreateBuilder(args);

// In development, load ../server/.env and map to ASP.NET config keys.
// This mirrors how docker-compose maps env vars in production.
if (builder.Environment.IsDevelopment())
{
    var envPath = Path.Combine(builder.Environment.ContentRootPath, "..", ".env");
    if (File.Exists(envPath))
    {
        var envVars = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(envPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;
            var eqIdx = trimmed.IndexOf('=');
            if (eqIdx <= 0)
                continue;
            var key = trimmed[..eqIdx];
            var value = trimmed[(eqIdx + 1)..];
            if (!string.IsNullOrEmpty(value))
                envVars[key] = value;
        }

        // Map to the same config keys docker-compose uses
        var mapped = new Dictionary<string, string?>();
        if (envVars.TryGetValue("POSTGRES_PASSWORD", out var pgPass))
        {
            var pgUser = envVars.GetValueOrDefault("POSTGRES_USER", "arrowthing");
            var pgDb = envVars.GetValueOrDefault("POSTGRES_DB", "arrowthing");
            var pgHost = envVars.GetValueOrDefault("POSTGRES_HOST", "localhost");
            mapped["ConnectionStrings:Default"] =
                $"Host={pgHost};Database={pgDb};Username={pgUser};Password={pgPass}";
        }
        if (envVars.TryGetValue("JWT_SECRET", out var jwtSecret))
            mapped["Jwt:Secret"] = jwtSecret;
        if (envVars.TryGetValue("ADMIN_API_KEY", out var adminKey))
            mapped["Admin:ApiKey"] = adminKey;
        if (envVars.TryGetValue("RESEND_API_KEY", out var resendKey))
            mapped["Resend:ApiKey"] = resendKey;
        if (envVars.TryGetValue("REDIS_CONNECTION_STRING", out var redisConn))
            mapped["Redis:ConnectionString"] = redisConn;

        if (mapped.Count > 0)
            builder.Configuration.AddInMemoryCollection(mapped);
    }
}

builder.Host.UseSerilog(
    (context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "ArrowThing")
            .WriteTo.Console();

        var lokiUrl = context.Configuration["Loki:Url"];
        if (!string.IsNullOrEmpty(lokiUrl))
        {
            configuration.WriteTo.GrafanaLoki(
                lokiUrl,
                labels: new List<LokiLabel>
                {
                    new LokiLabel { Key = "app", Value = "arrow-thing" },
                }
            );
        }
    }
);

// OpenTelemetry metrics
builder
    .Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter();
    });

// Database
// connectionString may be null in test environments where TestFactory replaces the
// DbContext registration entirely. In production, docker-compose always provides it.
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Redis — always register so DI validation passes; connect lazily at first resolve
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connStr = config["Redis:ConnectionString"];
    if (string.IsNullOrEmpty(connStr))
        throw new InvalidOperationException(
            "Redis:ConnectionString is not configured but IConnectionMultiplexer was resolved."
        );
    return ConnectionMultiplexer.Connect(connStr);
});

// HTTP client for Resend
builder.Services.AddHttpClient();

// Auth services
builder.Services.AddSingleton<JwtHelper>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<LeaderboardService>();
builder.Services.AddSingleton<LeaderboardCache>(sp => new LeaderboardCache(
    sp.GetService<IConnectionMultiplexer>()
));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddAuthorization();

// Configure JWT validation after all config sources are registered
builder
    .Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtHelper>(
        (options, jwt) =>
        {
            options.TokenValidationParameters = jwt.GetValidationParameters();
        }
    );

var app = builder.Build();

// Apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseSerilogRequestLogging();
app.UseAuthentication();
app.UseAuthorization();

// Validate security stamp on authenticated requests — rejects tokens issued before a stamp change
app.Use(
    async (context, next) =>
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var stampClaim = user.FindFirstValue("security_stamp");
            var userIdClaim = user.FindFirstValue(ClaimTypes.NameIdentifier);

            if (stampClaim != null && userIdClaim != null)
            {
                var db = context.RequestServices.GetRequiredService<AppDbContext>();
                var dbUser = await db.Users.FindAsync(Guid.Parse(userIdClaim));

                if (dbUser == null || dbUser.SecurityStamp != stampClaim)
                {
                    var audit = context.RequestServices.GetRequiredService<AuditLogService>();
                    await audit.LogAsync(
                        AuditEvent.SessionInvalidated,
                        Guid.Parse(userIdClaim),
                        dbUser?.Email,
                        GetClientIp(context)
                    );

                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(
                        new { error = "Session invalidated. Please log in again." }
                    );
                    return;
                }
            }
        }

        await next();
    }
);

// Endpoints
app.MapGet("/health", () => Results.Ok());

app.MapPost(
    "/api/auth/register",
    async (RegisterRequest request, AuthService auth, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.RegisterAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/login",
    async (LoginRequest request, AuthService auth, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.LoginAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapGet(
        "/api/auth/me",
        async (AuthService auth, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await auth.GetMeAsync(userId);
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapPatch(
        "/api/auth/me",
        async (
            UpdateDisplayNameRequest request,
            AuthService auth,
            ClaimsPrincipal user,
            HttpContext ctx
        ) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await auth.UpdateDisplayNameAsync(
                userId,
                request,
                GetClientIp(ctx)
            );
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapPost(
    "/api/auth/verify-code",
    async (VerifyCodeRequest request, AuthService auth, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.VerifyCodeAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/resend-verification",
    async (ResendVerificationRequest request, AuthService auth, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.ResendVerificationAsync(
            request,
            GetClientIp(ctx)
        );
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/forgot-password",
    async (ForgotPasswordRequest request, AuthService auth, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.ForgotPasswordAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/reset-password",
    async (ResetPasswordRequest request, AuthService auth, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.ResetPasswordAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
        "/api/auth/change-email",
        async (
            ChangeEmailRequest request,
            AuthService auth,
            ClaimsPrincipal user,
            HttpContext ctx
        ) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await auth.ChangeEmailAsync(
                userId,
                request,
                GetClientIp(ctx)
            );
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapPost(
        "/api/auth/change-password",
        async (
            ChangePasswordRequest request,
            AuthService auth,
            ClaimsPrincipal user,
            HttpContext ctx
        ) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await auth.ChangePasswordAsync(
                userId,
                request,
                GetClientIp(ctx)
            );
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapPost(
        "/api/auth/confirm-email-change",
        async (
            ConfirmEmailChangeRequest request,
            AuthService auth,
            ClaimsPrincipal user,
            HttpContext ctx
        ) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await auth.ConfirmEmailChangeAsync(
                userId,
                request,
                GetClientIp(ctx)
            );
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

// Admin endpoints (protected by API key, not JWT)
app.MapPost(
    "/api/admin/lock-account",
    async (LockAccountRequest request, AuthService auth, IConfiguration config, HttpContext ctx) =>
    {
        if (!VerifyAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var (response, status, error) = await auth.LockAccountAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/admin/unlock-account",
    async (LockAccountRequest request, AuthService auth, IConfiguration config, HttpContext ctx) =>
    {
        if (!VerifyAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var (response, status, error) = await auth.UnlockAccountAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

// Admin: score moderation
app.MapGet(
    "/api/admin/flagged-users",
    async (AppDbContext db, IConfiguration config, HttpContext ctx) =>
    {
        if (!VerifyAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var users = await db
            .Users.Where(u => u.Flagged)
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.FlagReason,
                u.CreatedAt,
            })
            .ToListAsync();

        return Results.Ok(users);
    }
);

app.MapPost(
    "/api/admin/users/{id:guid}/unflag",
    async (Guid id, AppDbContext db, IConfiguration config, HttpContext ctx) =>
    {
        if (!VerifyAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var user = await db.Users.FindAsync(id);
        if (user == null)
            return Results.NotFound();

        user.Flagged = false;
        user.FlagReason = null;
        await db.SaveChangesAsync();
        return Results.Ok(new { message = "User unflagged." });
    }
);

app.MapPost(
    "/api/admin/scores/{id:guid}/remove",
    async (
        Guid id,
        AppDbContext db,
        LeaderboardCache cache,
        IConfiguration config,
        HttpContext ctx
    ) =>
    {
        if (!VerifyAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var score = await db.Scores.FindAsync(id);
        if (score == null)
            return Results.NotFound();

        var width = score.BoardWidth;
        var height = score.BoardHeight;
        db.Scores.Remove(score);
        await db.SaveChangesAsync();
        cache.Invalidate(width, height);
        return Results.Ok(new { message = "Score removed." });
    }
);

// Score submission
app.MapPost(
        "/api/scores",
        async (SubmitReplayRequest request, GameService games, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await games.SubmitReplayAsync(
                userId,
                request.ReplayJson
            );
            if (response == null)
                return Results.Json(new { error }, statusCode: status);
            return status == 202 ? Results.Json(response, statusCode: 202) : Results.Ok(response);
        }
    )
    .RequireAuthorization();

// Score verification status
app.MapGet(
        "/api/scores/{gameId}/status",
        async (string gameId, HttpContext ctx) =>
        {
            var redis = ctx.RequestServices.GetService<IConnectionMultiplexer>();
            if (redis == null)
                return Results.Json(new { status = "pending" }, statusCode: 200);

            var db = redis.GetDatabase();
            var result = await db.StringGetAsync($"verify:result:{gameId}");
            if (result.IsNullOrEmpty)
                return Results.Json(new { status = "pending" }, statusCode: 200);

            var payload = System.Text.Json.JsonSerializer.Deserialize<object>((string)result!);
            return Results.Ok(payload);
        }
    )
    .RequireAuthorization();

// Leaderboards
app.MapGet(
    "/api/leaderboards/{w:int}x{h:int}",
    async (int w, int h, LeaderboardService leaderboards) =>
    {
        if (w < 2 || h < 2 || w > 400 || h > 400)
            return Results.Ok(new LeaderboardResponse());
        var result = await leaderboards.GetTopEntriesAsync(w, h);
        return Results.Ok(result);
    }
);

app.MapGet(
    "/api/leaderboards/all",
    async (LeaderboardService leaderboards) =>
    {
        var result = await leaderboards.GetTopEntriesAllAsync();
        return Results.Ok(result);
    }
);

app.MapGet(
        "/api/leaderboards/{w:int}x{h:int}/me",
        async (int w, int h, LeaderboardService leaderboards, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var entry = await leaderboards.GetPlayerEntryAsync(userId, w, h);
            return entry != null ? Results.Ok(entry) : Results.NotFound();
        }
    )
    .RequireAuthorization();

app.MapGet(
        "/api/leaderboards/all/me",
        async (LeaderboardService leaderboards, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var entry = await leaderboards.GetPlayerEntryAllAsync(userId);
            return entry != null ? Results.Ok(entry) : Results.NotFound();
        }
    )
    .RequireAuthorization();

// Replay fetch
app.MapGet(
    "/api/replays/{gameId}",
    async (string gameId, LeaderboardService leaderboards) =>
    {
        var replayJson = await leaderboards.GetReplayAsync(gameId);
        return replayJson != null ? Results.Ok(new { replayJson }) : Results.NotFound();
    }
);

app.MapPrometheusScrapingEndpoint("/metrics");

app.Run();

static string? GetClientIp(HttpContext ctx)
{
    // Nginx forwards the real client IP from Cloudflare's CF-Connecting-IP header
    // as X-Forwarded-For. Fall back to connection remote IP for direct access.
    return ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()
        ?? ctx.Connection.RemoteIpAddress?.ToString();
}

// Make the implicit Program class accessible to integration tests.
public partial class Program { }
