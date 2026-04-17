using System.Security.Cryptography;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace ArrowThing.Server.Auth;

/// <summary>
/// Issues, redeems, and revokes opaque refresh tokens. Pair the rotated raw
/// token with a fresh JWT access token in the calling auth flow.
///
/// Token format on the wire is <c>{rowIdBase64Url}.{secretBase64Url}</c>:
/// the left half identifies the row (so redemption is O(1)), the right half
/// is the secret that's bcrypt-verified against <c>RefreshToken.TokenHash</c>.
/// </summary>
public class RefreshTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
    private const int SecretByteLength = 32;
    private const char Separator = '.';

    private readonly AppDbContext _db;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(AppDbContext db, ILogger<RefreshTokenService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Mints a new refresh token for the given user. Caller commits the
    /// transaction (the row is added to the tracker but not saved here, so
    /// the call composes with other auth-flow updates).
    /// </summary>
    public RefreshTokenIssue Issue(Guid userId, string? userAgent)
    {
        var id = Guid.NewGuid();
        var secret = GenerateSecret();
        var now = DateTime.UtcNow;
        var row = new RefreshToken
        {
            Id = id,
            UserId = userId,
            TokenHash = PasswordHasher.HashOtp(secret),
            IssuedAt = now,
            ExpiresAt = now + Lifetime,
            UserAgent = Truncate(userAgent, 512),
        };
        _db.RefreshTokens.Add(row);
        return new RefreshTokenIssue(EncodeToken(id, secret), row.ExpiresAt);
    }

    /// <summary>
    /// Validates and rotates the supplied raw token. Returns the new
    /// <see cref="RefreshTokenIssue"/> + owning user on success. Returns
    /// <c>(null, null)</c> on any failure path; the caller maps that to 401
    /// without leaking which sub-check failed.
    ///
    /// Rotation: the presented row is marked revoked + ReplacedBy, and a
    /// fresh row is added. Reuse of an already-revoked token revokes every
    /// active token for the user (suspected theft).
    /// </summary>
    public async Task<(RefreshTokenIssue? Issue, User? User)> RedeemAsync(
        string raw,
        string? userAgent
    )
    {
        if (!TryDecode(raw, out var id, out var secret))
            return (null, null);

        var row = await _db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.Id == id);
        if (row == null || !PasswordHasher.VerifyOtp(secret, row.TokenHash))
            return (null, null);

        if (row.RevokedAt != null)
        {
            // The token was already redeemed (or explicitly revoked). If it
            // was rotated (ReplacedByTokenId set) and someone is presenting
            // the old half, treat it as a possible theft and burn the whole
            // tree of tokens for this user.
            _logger.LogWarning(
                "Refresh token reuse detected for user {UserId}; revoking all active tokens",
                row.UserId
            );
            await RevokeAllActiveAsync(row.UserId);
            return (null, null);
        }

        if (row.ExpiresAt <= DateTime.UtcNow)
            return (null, null);

        var newSecret = GenerateSecret();
        var newId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var newRow = new RefreshToken
        {
            Id = newId,
            UserId = row.UserId,
            TokenHash = PasswordHasher.HashOtp(newSecret),
            IssuedAt = now,
            ExpiresAt = now + Lifetime,
            UserAgent = Truncate(userAgent, 512),
        };
        _db.RefreshTokens.Add(newRow);

        row.RevokedAt = now;
        row.ReplacedByTokenId = newId;

        return (new RefreshTokenIssue(EncodeToken(newId, newSecret), newRow.ExpiresAt), row.User);
    }

    /// <summary>
    /// Revokes a single refresh token. Idempotent — succeeds quietly if the
    /// token is unknown / malformed / already revoked.
    /// </summary>
    public async Task RevokeAsync(string raw)
    {
        if (!TryDecode(raw, out var id, out var secret))
            return;

        var row = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.Id == id);
        if (row == null || row.RevokedAt != null)
            return;
        if (!PasswordHasher.VerifyOtp(secret, row.TokenHash))
            return;

        row.RevokedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Revokes every active refresh token for the user. Used by reuse
    /// detection here, and by <c>AuthService</c> when the SecurityStamp
    /// rotates (password change, lockout, …).
    /// </summary>
    public Task RevokeAllActiveAsync(Guid userId)
    {
        var now = DateTime.UtcNow;
        return _db
            .RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
    }

    private static string GenerateSecret()
    {
        var bytes = new byte[SecretByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string EncodeToken(Guid id, string secret) =>
        $"{Base64UrlEncode(id.ToByteArray())}{Separator}{secret}";

    private static bool TryDecode(string raw, out Guid id, out string secret)
    {
        id = default;
        secret = "";
        if (string.IsNullOrEmpty(raw))
            return false;

        var sepIdx = raw.IndexOf(Separator);
        if (sepIdx <= 0 || sepIdx >= raw.Length - 1)
            return false;

        try
        {
            var idBytes = Base64UrlDecode(raw[..sepIdx]);
            if (idBytes.Length != 16)
                return false;
            id = new Guid(idBytes);
            secret = raw[(sepIdx + 1)..];
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }
        return Convert.FromBase64String(padded);
    }

    private static string? Truncate(string? s, int max) =>
        s == null ? null
        : s.Length <= max ? s
        : s[..max];
}

public record RefreshTokenIssue(string Token, DateTime ExpiresAt);
