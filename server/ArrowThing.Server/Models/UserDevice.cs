namespace ArrowThing.Server.Models;

/// <summary>
/// A device id that has completed the email OTP challenge for a user. On
/// subsequent logins from the same <c>X-Device-Id</c>, we skip the OTP step.
/// The raw device id is never stored — only a bcrypt hash, because possession
/// of the raw value is effectively half of 2FA.
/// </summary>
public class UserDevice
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string DeviceIdHash { get; set; } = "";
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? UserAgent { get; set; }

    public User User { get; set; } = null!;
}
