namespace ArrowThing.Server.Games;

/// <summary>
/// Result of endless-replay verification. Mirrors <see cref="VerificationResult"/>
/// but carries endless-specific verified totals (Clears + LongestCombo +
/// DurationSeconds) instead of solve time.
/// </summary>
public sealed class EndlessVerificationResult
{
    public bool IsValid { get; }
    public string? Reason { get; }

    /// <summary>Server-verified clear count. Persisted as the leaderboard sort key.</summary>
    public int VerifiedClears { get; }

    /// <summary>Server-verified longest combo. Telemetry-only.</summary>
    public int VerifiedLongestCombo { get; }

    /// <summary>Server-verified run duration in seconds. Tiebreak when clears tie.</summary>
    public double VerifiedDurationSeconds { get; }

    private EndlessVerificationResult(
        bool isValid,
        string? reason,
        int clears,
        int longestCombo,
        double durationSeconds
    )
    {
        IsValid = isValid;
        Reason = reason;
        VerifiedClears = clears;
        VerifiedLongestCombo = longestCombo;
        VerifiedDurationSeconds = durationSeconds;
    }

    public static EndlessVerificationResult Valid(
        int clears,
        int longestCombo,
        double durationSeconds
    ) => new(true, null, clears, longestCombo, durationSeconds);

    public static EndlessVerificationResult Invalid(string reason) => new(false, reason, 0, 0, 0.0);
}
