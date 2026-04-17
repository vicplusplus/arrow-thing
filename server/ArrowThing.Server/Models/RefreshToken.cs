namespace ArrowThing.Server.Models;

/// <summary>
/// Long-lived refresh credential paired with a short-lived JWT access token.
/// The raw token is never stored — only a bcrypt hash of the secret half.
/// Rotated on every redemption (see <c>RefreshTokenService.RedeemAsync</c>);
/// reuse of an already-rotated token revokes every active token for the user
/// (suspected theft).
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// Set on rotation: points at the row that replaced this one. Used as the
    /// signal for "this token has already been used" reuse detection.
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    public string? UserAgent { get; set; }

    public User User { get; set; } = null!;

    public bool IsActive => RevokedAt == null && ExpiresAt > DateTime.UtcNow;
}
