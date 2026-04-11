namespace ArrowThing.Server.Games;

/// <summary>
/// Result of replay verification. Contains validity status,
/// a reason string on failure, and the server-computed solve time on success.
/// </summary>
public sealed class VerificationResult
{
    public bool IsValid { get; }
    public string? Reason { get; }
    public double VerifiedTime { get; }

    private VerificationResult(bool isValid, string? reason, double verifiedTime)
    {
        IsValid = isValid;
        Reason = reason;
        VerifiedTime = verifiedTime;
    }

    public static VerificationResult Valid(double verifiedTime) =>
        new VerificationResult(true, null, verifiedTime);

    public static VerificationResult Invalid(string reason) =>
        new VerificationResult(false, reason, 0.0);
}
