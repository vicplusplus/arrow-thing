using System.Security.Claims;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Database
// connectionString may be null in test environments where TestFactory replaces the
// DbContext registration entirely. In production, docker-compose always provides it.
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// HTTP client for Resend
builder.Services.AddHttpClient();

// Auth services
builder.Services.AddSingleton<JwtHelper>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<AuthService>();

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
        var (response, status, error) = await auth.ForgotPasswordAsync(
            request,
            GetClientIp(ctx)
        );
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
        async (ChangeEmailRequest request, AuthService auth, ClaimsPrincipal user, HttpContext ctx) =>
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
        var adminKey = config["Admin:ApiKey"];
        if (string.IsNullOrEmpty(adminKey))
            return Results.Json(new { error = "Admin API key not configured." }, statusCode: 500);

        var provided = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (provided != adminKey)
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
        var adminKey = config["Admin:ApiKey"];
        if (string.IsNullOrEmpty(adminKey))
            return Results.Json(new { error = "Admin API key not configured." }, statusCode: 500);

        var provided = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
        if (provided != adminKey)
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var (response, status, error) = await auth.UnlockAccountAsync(request, GetClientIp(ctx));
        return response != null
            ? Results.Ok(response)
            : Results.Json(new { error }, statusCode: status);
    }
);

// Admin dashboard — JSON API endpoints for stats, users, and audit logs.
// All protected by X-Admin-Key header, same as lock/unlock.

app.MapGet(
    "/api/admin/stats",
    async (IConfiguration config, HttpContext ctx, AppDbContext db) =>
    {
        if (!ValidateAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var now = DateTime.UtcNow;
        var totalUsers = await db.Users.CountAsync();
        var verifiedUsers = await db.Users.CountAsync(u => u.EmailVerifiedAt != null);
        var lockedUsers = await db.Users.CountAsync(u => u.LockedAt != null);
        var last24h = now.AddHours(-24);
        var last7d = now.AddDays(-7);
        var registrations24h = await db.Users.CountAsync(u => u.CreatedAt >= last24h);
        var registrations7d = await db.Users.CountAsync(u => u.CreatedAt >= last7d);
        var logins24h = await db.AuditLogs.CountAsync(e =>
            e.Event == AuditEvent.Login && e.Timestamp >= last24h
        );
        var logins7d = await db.AuditLogs.CountAsync(e =>
            e.Event == AuditEvent.Login && e.Timestamp >= last7d
        );
        var failedLogins24h = await db.AuditLogs.CountAsync(e =>
            e.Event == AuditEvent.LoginFailed && e.Timestamp >= last24h
        );

        return Results.Ok(
            new
            {
                totalUsers,
                verifiedUsers,
                lockedUsers,
                registrations24h,
                registrations7d,
                logins24h,
                logins7d,
                failedLogins24h,
                serverTime = now,
            }
        );
    }
);

app.MapGet(
    "/api/admin/users",
    async (IConfiguration config, HttpContext ctx, AppDbContext db, string? search, int? page) =>
    {
        if (!ValidateAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var pageSize = 50;
        var pageNum = Math.Max(1, page ?? 1);

        IQueryable<User> query = db.Users;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(u => u.Email.Contains(term) || u.DisplayName.Contains(term));
        }

        var total = await query.CountAsync();
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((pageNum - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.Email,
                u.DisplayName,
                u.CreatedAt,
                u.EmailVerifiedAt,
                u.LockedAt,
            })
            .ToListAsync();

        return Results.Ok(new { total, page = pageNum, pageSize, users });
    }
);

app.MapGet(
    "/api/admin/users/{id:guid}",
    async (Guid id, IConfiguration config, HttpContext ctx, AppDbContext db) =>
    {
        if (!ValidateAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var user = await db.Users.FindAsync(id);
        if (user == null)
            return Results.Json(new { error = "User not found." }, statusCode: 404);

        var recentLogs = await db
            .AuditLogs.Where(l => l.UserId == id)
            .OrderByDescending(l => l.Timestamp)
            .Take(50)
            .Select(l => new
            {
                l.Timestamp,
                l.Event,
                l.IpAddress,
                l.Detail,
            })
            .ToListAsync();

        return Results.Ok(
            new
            {
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.CreatedAt,
                    user.EmailVerifiedAt,
                    user.LockedAt,
                    hasPendingEmailChange = user.PendingEmail != null,
                },
                recentLogs,
            }
        );
    }
);

app.MapGet(
    "/api/admin/audit-log",
    async (
        IConfiguration config,
        HttpContext ctx,
        AppDbContext db,
        string? eventType,
        int? page,
        string? search
    ) =>
    {
        if (!ValidateAdminKey(config, ctx))
            return Results.Json(new { error = "Unauthorized." }, statusCode: 401);

        var pageSize = 100;
        var pageNum = Math.Max(1, page ?? 1);

        IQueryable<AuditLog> query = db.AuditLogs;
        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(l => l.Event == eventType);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(l => l.Email != null && l.Email.Contains(term));
        }

        var total = await query.CountAsync();
        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Skip((pageNum - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.Timestamp,
                l.Event,
                l.UserId,
                l.Email,
                l.IpAddress,
                l.Detail,
            })
            .ToListAsync();

        return Results.Ok(new { total, page = pageNum, pageSize, logs });
    }
);

// Self-contained HTML admin dashboard
app.MapGet(
    "/api/admin/dashboard",
    (IConfiguration config, HttpContext ctx) =>
    {
        // Dashboard HTML embeds the admin key entry — no server-side auth check on the page itself.
        // All data fetches from the page use X-Admin-Key header, so the key is validated per-request.
        return Results.Content(AdminDashboardHtml.Page, "text/html");
    }
);

app.Run();

static string? GetClientIp(HttpContext ctx)
{
    // Nginx forwards the real client IP from Cloudflare's CF-Connecting-IP header
    // as X-Forwarded-For. Fall back to connection remote IP for direct access.
    return ctx.Request.Headers["X-Forwarded-For"].FirstOrDefault()
        ?? ctx.Connection.RemoteIpAddress?.ToString();
}

static bool ValidateAdminKey(IConfiguration config, HttpContext ctx)
{
    var adminKey = config["Admin:ApiKey"];
    if (string.IsNullOrEmpty(adminKey))
        return false;
    var provided = ctx.Request.Headers["X-Admin-Key"].FirstOrDefault();
    return provided == adminKey;
}

// Make the implicit Program class accessible to integration tests.
public partial class Program { }
