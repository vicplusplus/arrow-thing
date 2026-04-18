using System.Net.WebSockets;
using System.Security.Claims;
using ArrowThing.Server;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Coop;
using ArrowThing.Server.Data;
using ArrowThing.Server.Games;
using ArrowThing.Server.Leaderboards;
using ArrowThing.Server.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using Serilog;
using Serilog.Sinks.Grafana.Loki;
using StackExchange.Redis;

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

// Redis — optional. Registered with AbortOnConnectFail=false so a Redis outage
// at startup doesn't crash the API; the multiplexer reconnects in the
// background and surfaces availability via IConnectionMultiplexer.IsConnected
// (see RedisExtensions.IsAvailable). Missing config registers null.
// Factory may return null when degraded — consumers must declare the
// dependency as IConnectionMultiplexer? and gate with RedisExtensions.IsAvailable.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var connStr = config["Redis:ConnectionString"];
    if (string.IsNullOrEmpty(connStr))
    {
        logger.LogWarning("Redis:ConnectionString not configured; running without Redis.");
        return null!;
    }
    try
    {
        var options = ConfigurationOptions.Parse(connStr);
        options.AbortOnConnectFail = false;
        return ConnectionMultiplexer.Connect(options);
    }
    catch (Exception ex)
    {
        // AbortOnConnectFail=false should prevent this, but StackExchange.Redis
        // can still throw on malformed config. Log and degrade.
        logger.LogError(ex, "Failed to construct Redis multiplexer; running degraded.");
        return null!;
    }
});

// HTTP client for Resend
builder.Services.AddHttpClient();

// Auth services
builder.Services.AddSingleton<JwtHelper>();
builder.Services.Configure<CookieAuthOptions>(builder.Configuration.GetSection("Auth:Cookies"));
builder.Services.AddSingleton<CookieIssuer>();

// CORS for the cookie-auth path. Credentials must be allowed, and the
// allowed origin list comes from config (cannot be wildcard when credentials
// are on). Bearer-only clients on same-origin don't need CORS.
const string CorsPolicyName = "ArrowThingClient";
var corsAllowedOrigins =
    builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? Array.Empty<string>();
builder.Services.AddCors(options =>
{
    options.AddPolicy(
        CorsPolicyName,
        policy =>
        {
            if (corsAllowedOrigins.Length > 0)
                policy.WithOrigins(corsAllowedOrigins);
            policy
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .WithExposedHeaders(ExceptionHandlingMiddleware.CorrelationIdHeader);
        }
    );
});

// Response compression for the JSON-heavy read endpoints. Brotli > Gzip when
// both are accepted; falls back to Gzip for older clients. Replays + the
// global leaderboard are the largest payloads; all JSON paths benefit.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.MimeTypes = ["application/json", "application/json; charset=utf-8", "text/json"];
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest
);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(
    o => o.Level = System.IO.Compression.CompressionLevel.Fastest
);

builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<GameService>();
builder.Services.AddScoped<LeaderboardService>();
builder.Services.AddScoped<LobbyService>();
builder.Services.AddScoped<LobbySnapshotRepository>();
builder.Services.AddScoped<LobbyReplayService>();
builder.Services.AddSingleton<AccountConcurrencyLimiter>();
builder.Services.AddSingleton<GenerationProgressBus>();
builder.Services.AddSingleton<CoopHub>();
builder.Services.Configure<LobbyOptions>(builder.Configuration.GetSection("Lobby"));
builder.Services.AddSingleton<LeaderboardCache>(sp => new LeaderboardCache(
    sp.GetService<IConnectionMultiplexer>()
));

// Phase 4: lazy backfill of legacy ReplayJson text rows into the gzipped
// bytea column. Idempotent — exits when no rows remain.
builder.Services.AddHostedService<ArrowThing.Server.Games.ReplayBackfillService>();
builder.Services.AddHostedService<LobbyRetentionService>();

// Production-secret guard. Must run after all config sources have been added so the
// Jwt:Secret / Admin:ApiKey values are visible. Blocks deploys that boot with empty,
// short, or known-dev-default secrets before any request can be served.
if (builder.Environment.IsProduction())
{
    StartupSecretValidator.ValidateProductionSecret(
        builder.Configuration["Jwt:Secret"],
        name: "Jwt:Secret",
        minLength: 32,
        blocklist: ["local-dev-jwt-secret-not-for-production"]
    );
    StartupSecretValidator.ValidateProductionSecret(
        builder.Configuration["Admin:ApiKey"],
        name: "Admin:ApiKey",
        minLength: 16,
        blocklist: ["local-dev-admin-key"]
    );
}

// Trust the X-Forwarded-Proto / X-Forwarded-For headers from nginx so
// UseHttpsRedirection and GetClientIp see the real client-side values. The
// known-proxy allowlist is empty (we clear it) because the request chain in
// production is Cloudflare → nginx → Kestrel, all on the same host network.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(365);
        options.IncludeSubDomains = true;
        options.Preload = true;
    });
}

builder
    .Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, AdminKeyAuthenticationHandler>(
        AdminKeyAuthenticationHandler.SchemeName,
        _ => { }
    );

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        AdminKeyAuthenticationHandler.SchemeName,
        policy =>
            policy
                .AddAuthenticationSchemes(AdminKeyAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
    );
});

// Configure JWT validation after all config sources are registered.
// OnMessageReceived reads the token from the arrow_access cookie when no
// Authorization: Bearer header is present, so WebGL cookie-auth clients work
// alongside the legacy bearer path.
builder
    .Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtHelper>(
        (options, jwt) =>
        {
            options.TokenValidationParameters = jwt.GetValidationParameters();
            options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    // Only fall back to the cookie when there's no
                    // Authorization header at all. Setting ctx.Token here
                    // short-circuits JwtBearer's header lookup, so we must
                    // inspect the header explicitly — the default token
                    // field is still empty at this point in the pipeline.
                    var authHeader = ctx.Request.Headers.Authorization.ToString();
                    if (string.IsNullOrEmpty(authHeader))
                    {
                        var cookie = ctx.Request.Cookies[CookieIssuer.AccessCookieName];
                        if (!string.IsNullOrEmpty(cookie))
                            ctx.Token = cookie;
                    }
                    return Task.CompletedTask;
                },
            };
        }
    );

var app = builder.Build();

// Apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Subscribe CoopHub to the generation progress bus.
{
    var hub = app.Services.GetRequiredService<CoopHub>();
    await hub.EnsureSubscribedAsync();
}

// First in the pipeline: stamp a correlation id on every request and catch
// unhandled exceptions so clients see a consistent JSON error instead of
// leaking stack traces or connection resets.
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Trust proxy headers before any middleware that cares about scheme/IP.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging(opts =>
{
    // Don't log full URLs for /ws/coop/ — they contain JWTs in the query string.
    opts.GetLevel = (httpCtx, _, _) =>
        httpCtx.Request.Path.StartsWithSegments("/ws/coop")
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
});
app.UseWebSockets();

// CORS before auth so preflights don't need a JWT.
// Response compression must be early in the pipeline so it sees the body
// before serialization is finalized. Sits before CORS/auth/etc.
app.UseResponseCompression();

app.UseCors(CorsPolicyName);

// Defense-in-depth CSRF check: mutating verbs on cookie-authed requests
// must carry an Origin (or Referer) that matches the CORS allow-list.
// Bearer-authed calls skip this — they come from non-browser clients that
// don't send Origin / can't be tricked by a rogue page.
var allowedOriginSet = new HashSet<string>(corsAllowedOrigins, StringComparer.OrdinalIgnoreCase);
app.Use(
    async (context, next) =>
    {
        if (IsMutating(context.Request.Method) && HasAuthCookie(context.Request))
        {
            var origin = context.Request.Headers["Origin"].FirstOrDefault();
            var referer = context.Request.Headers["Referer"].FirstOrDefault();
            var candidate = !string.IsNullOrEmpty(origin) ? origin : GetOriginFromReferer(referer);
            // We only block when an Origin / Referer IS present and doesn't
            // match. Real browsers always tag cross-origin mutations with
            // Origin, so a missing header on a cookie-authed mutation is
            // either a same-origin fetch (safe), a non-browser client
            // replaying our cookies (implausible — they're HttpOnly), or a
            // test. Rejecting "missing Origin" would false-positive on the
            // test suite without adding real CSRF protection.
            if (!string.IsNullOrEmpty(candidate) && !allowedOriginSet.Contains(candidate))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Origin not allowed." });
                return;
            }
        }
        await next();
    }
);

app.UseAuthentication();

// Validate security stamp immediately after authentication so stale JWTs are
// treated as anonymous before authorization runs. Public endpoints (login,
// /health, etc.) keep working; protected endpoints fail authorization
// naturally. Running before UseAuthorization is important — otherwise a
// stale cookie/JWT would reject a public endpoint like POST /api/auth/login
// with 401 mid-flow.
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

                    // Strip the stale identity; authorization will reject
                    // protected endpoints, public endpoints keep working.
                    context.User = new System.Security.Claims.ClaimsPrincipal(
                        new System.Security.Claims.ClaimsIdentity()
                    );
                }
            }
        }

        await next();
    }
);

app.UseAuthorization();

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
    async (LoginRequest request, AuthService auth, CookieIssuer cookies, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.LoginAsync(
            request,
            GetClientIp(ctx),
            deviceId: ctx.Request.Headers["X-Device-Id"].FirstOrDefault(),
            userAgent: ctx.Request.Headers["User-Agent"].FirstOrDefault()
        );
        if (response is AuthResponse auth200)
            IssueAuthCookies(cookies, ctx.Response, auth200);
        return response != null
            ? Results.Json(response, statusCode: status)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/verify-device",
    async (VerifyDeviceRequest request, AuthService auth, CookieIssuer cookies, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.VerifyDeviceAsync(
            request,
            GetClientIp(ctx),
            deviceId: ctx.Request.Headers["X-Device-Id"].FirstOrDefault(),
            userAgent: ctx.Request.Headers["User-Agent"].FirstOrDefault()
        );
        if (response != null)
            IssueAuthCookies(cookies, ctx.Response, response);
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/refresh",
    async (RefreshTokenRequest request, AuthService auth, CookieIssuer cookies, HttpContext ctx) =>
    {
        // Accept the token from the request body OR the arrow_refresh cookie.
        // WebGL clients send only the cookie; editor / Bearer clients send the body.
        var token = !string.IsNullOrEmpty(request?.RefreshToken)
            ? request.RefreshToken
            : ctx.Request.Cookies[CookieIssuer.RefreshCookieName] ?? "";
        var (response, status, error) = await auth.RefreshAsync(
            token,
            userAgent: ctx.Request.Headers["User-Agent"].FirstOrDefault()
        );
        if (response != null)
            IssueAuthCookies(cookies, ctx.Response, response);
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

app.MapPost(
    "/api/auth/logout",
    async (LogoutRequest request, AuthService auth, CookieIssuer cookies, HttpContext ctx) =>
    {
        var token = !string.IsNullOrEmpty(request?.RefreshToken)
            ? request.RefreshToken
            : ctx.Request.Cookies[CookieIssuer.RefreshCookieName] ?? "";
        var (response, status) = await auth.LogoutAsync(token);
        cookies.ClearCookies(ctx.Response);
        return Results.Json(response, statusCode: status);
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

app.MapPatch(
        "/api/auth/me/coop-color",
        async (
            UpdateCoopColorRequest request,
            AuthService auth,
            ClaimsPrincipal user,
            HttpContext ctx
        ) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await auth.UpdateCoopColorAsync(
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
    async (VerifyCodeRequest request, AuthService auth, CookieIssuer cookies, HttpContext ctx) =>
    {
        var (response, status, error) = await auth.VerifyCodeAsync(
            request,
            GetClientIp(ctx),
            deviceId: ctx.Request.Headers["X-Device-Id"].FirstOrDefault(),
            userAgent: ctx.Request.Headers["User-Agent"].FirstOrDefault()
        );
        if (response != null)
            IssueAuthCookies(cookies, ctx.Response, response);
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
        async (LockAccountRequest request, AuthService auth, HttpContext ctx) =>
        {
            var (response, status, error) = await auth.LockAccountAsync(request, GetClientIp(ctx));
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization(AdminKeyAuthenticationHandler.SchemeName);

app.MapPost(
        "/api/admin/unlock-account",
        async (LockAccountRequest request, AuthService auth, HttpContext ctx) =>
        {
            var (response, status, error) = await auth.UnlockAccountAsync(
                request,
                GetClientIp(ctx)
            );
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization(AdminKeyAuthenticationHandler.SchemeName);

// Admin: score moderation
app.MapGet(
        "/api/admin/flagged-users",
        async (AppDbContext db) =>
        {
            var users = await db
                .Users.Where(u => u.Flagged)
                .OrderByDescending(u => u.CreatedAt)
                .Select(u => new
                {
                    u.Id,
                    u.Email,
                    u.DisplayName,
                    u.FlagReason,
                    u.FlaggedAt,
                    u.FlagTriggerSeed,
                    u.FlagTriggerBoardWidth,
                    u.FlagTriggerBoardHeight,
                    u.FlagTriggerGameId,
                    u.CreatedAt,
                })
                .ToListAsync();

            return Results.Ok(users);
        }
    )
    .RequireAuthorization(AdminKeyAuthenticationHandler.SchemeName);

app.MapPost(
        "/api/admin/users/{id:guid}/unflag",
        async (Guid id, AppDbContext db) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user == null)
                return Results.NotFound();

            user.Flagged = false;
            user.FlagReason = null;
            user.FlaggedAt = null;
            user.FlagTriggerSeed = null;
            user.FlagTriggerBoardWidth = null;
            user.FlagTriggerBoardHeight = null;
            user.FlagTriggerGameId = null;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "User unflagged." });
        }
    )
    .RequireAuthorization(AdminKeyAuthenticationHandler.SchemeName);

app.MapPost(
        "/api/admin/scores/{id:guid}/remove",
        async (Guid id, AppDbContext db, LeaderboardCache cache) =>
        {
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
    )
    .RequireAuthorization(AdminKeyAuthenticationHandler.SchemeName);

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
            if (!redis.IsAvailable())
                return Results.Json(
                    new { error = "Service temporarily unavailable." },
                    statusCode: 503
                );

            var db = redis!.GetDatabase();
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

// -- Co-op lobbies (Phase 3) -------------------------------------------------

app.MapPost(
        "/api/lobbies",
        async (CreateLobbyRequest request, LobbyService lobbies, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await lobbies.CreateAsync(userId, request);
            return response != null
                ? Results.Json(response, statusCode: status)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapGet(
        "/api/lobbies/me",
        async (
            LobbyService lobbies,
            ClaimsPrincipal user,
            string? filter,
            string? sort,
            int? page
        ) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await lobbies.ListMineAsync(
                userId,
                filter,
                sort,
                page ?? 0
            );
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapGet(
        "/api/lobbies/{code}",
        async (string code, LobbyService lobbies) =>
        {
            var (response, status, error) = await lobbies.GetByCodeAsync(code);
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapGet(
        "/api/lobbies/{code}/replay",
        async (string code, LobbyReplayService replays, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (replay, status, error) = await replays.BuildAsync(code, userId);
            if (replay == null)
                return Results.Json(new { error }, statusCode: status);
            // ReplayData uses public fields, which System.Text.Json's default
            // ASP.NET settings don't serialize. Round-trip through Newtonsoft
            // (the canonical solo-save serializer) to keep wire + disk
            // formats identical.
            return Results.Content(replay.ToJson(), "application/json");
        }
    )
    .RequireAuthorization();

app.MapPatch(
        "/api/lobbies/{id:guid}",
        async (Guid id, RenameLobbyRequest request, LobbyService lobbies, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await lobbies.RenameAsync(id, userId, request);
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapDelete(
        "/api/lobbies/{id:guid}",
        async (Guid id, LobbyService lobbies, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await lobbies.SoftDeleteAsync(id, userId);
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

app.MapPost(
        "/api/lobbies/{id:guid}/retry-gen",
        async (Guid id, LobbyService lobbies, ClaimsPrincipal user) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var (response, status, error) = await lobbies.RetryGenerationAsync(id, userId);
            return response != null
                ? Results.Ok(response)
                : Results.Json(new { error }, statusCode: status);
        }
    )
    .RequireAuthorization();

// WebSocket: /ws/coop/{code}?token={jwt}
app.MapGet(
    "/ws/coop/{code}",
    async (
        HttpContext ctx,
        string code,
        CoopHub hub,
        LobbyService lobbies,
        JwtHelper jwtHelper,
        AppDbContext db,
        ILoggerFactory loggerFactory
    ) =>
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
            return Results.BadRequest(new { error = "WebSocket request expected." });

        var token = ctx.Request.Query["token"].FirstOrDefault();
        var hubLogger = loggerFactory.CreateLogger("CoopHub");
        var userId = await CoopHub.ValidateTokenAsync(token, jwtHelper, db, hubLogger);
        if (userId == null)
        {
            ctx.Response.StatusCode = 401;
            return Results.Empty;
        }

        var lobby = await lobbies.FindActiveByCodeAsync(code);
        if (lobby == null)
        {
            ctx.Response.StatusCode = 404;
            return Results.Empty;
        }

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        await hub.HandleConnectionAsync(socket, lobby, userId.Value, ctx.RequestAborted);
        return Results.Empty;
    }
);

app.MapPrometheusScrapingEndpoint("/metrics");

// Test-only endpoint used to verify the global exception middleware. Only
// mapped when Test:EnableCrashEndpoint=true, which is set by TestFactory.
if (app.Configuration.GetValue<bool>("Test:EnableCrashEndpoint"))
{
    app.MapGet(
        "/__test/crash",
        () =>
        {
            throw new InvalidOperationException("intentional test crash");
#pragma warning disable CS0162 // unreachable
            return Results.Ok();
#pragma warning restore CS0162
        }
    );
}

app.Run();

static bool IsMutating(string method) =>
    HttpMethods.IsPost(method)
    || HttpMethods.IsPut(method)
    || HttpMethods.IsPatch(method)
    || HttpMethods.IsDelete(method);

static bool HasAuthCookie(HttpRequest request) =>
    !string.IsNullOrEmpty(request.Cookies[CookieIssuer.AccessCookieName])
    || !string.IsNullOrEmpty(request.Cookies[CookieIssuer.RefreshCookieName]);

static string? GetOriginFromReferer(string? referer)
{
    if (string.IsNullOrEmpty(referer))
        return null;
    if (!Uri.TryCreate(referer, UriKind.Absolute, out var uri))
        return null;
    return $"{uri.Scheme}://{uri.Authority}";
}

static void IssueAuthCookies(CookieIssuer cookies, HttpResponse response, object body)
{
    // The OTP-pending branch returns a DeviceOtpRequiredResponse without a
    // JWT; cookies are only issued on the fully-authenticated AuthResponse.
    if (body is AuthResponse auth && !string.IsNullOrEmpty(auth.Token))
    {
        cookies.IssueAccessCookie(response, auth.Token);
        if (!string.IsNullOrEmpty(auth.RefreshToken))
            cookies.IssueRefreshCookie(response, auth.RefreshToken);
    }
}

static string? GetClientIp(HttpContext ctx)
{
    // Nginx forwards the real client IP from Cloudflare's CF-Connecting-IP header
    // as X-Forwarded-For. Fall back to connection remote IP for direct access.
    return ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()
        ?? ctx.Connection.RemoteIpAddress?.ToString();
}

// Make the implicit Program class accessible to integration tests.
public partial class Program { }
