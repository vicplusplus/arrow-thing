namespace ArrowThing.Server.Models;

/// <summary>
/// Verified endless-mode personal best for one user at one board config.
/// Mirrors <see cref="Score"/>'s shape but carries endless-specific stat
/// columns (Clears / LongestCombo / DurationSeconds) instead of classic's
/// Time + MaxArrowLength.
///
/// Separate table because:
/// <list type="bullet">
///   <item>Sort key differs (Clears desc + DurationSeconds asc tiebreak,
///   not Time asc).</item>
///   <item>Endless doesn't use a board snapshot — boards grow from empty
///   during the run, reproduced by <see cref="EndlessRun"/> on the verifier.</item>
///   <item>Tuning version travels with the row so historical replays can
///   be re-verified under the tuning they were recorded under.</item>
/// </list>
/// </summary>
public class EndlessScore
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Client-generated UUID for the game that produced this PB.</summary>
    public Guid GameId { get; set; }

    /// <summary>Spawn seed — the verifier needs it to drive <see cref="EndlessRun"/> deterministically.</summary>
    public int Seed { get; set; }

    public int BoardWidth { get; set; }
    public int BoardHeight { get; set; }

    /// <summary>
    /// Tuning version this run was recorded under (see <c>EndlessTuning.ForVersion</c>).
    /// The verifier loads the matching historical tuning so old replays don't
    /// break when the live tuning evolves.
    /// </summary>
    public int TuningsVersion { get; set; }

    /// <summary>Primary leaderboard sort key (descending). Server-verified.</summary>
    public int Clears { get; set; }

    /// <summary>Telemetry. Not part of the PB rule.</summary>
    public int LongestCombo { get; set; }

    /// <summary>Tiebreak when <see cref="Clears"/> ties (ascending). Server-verified.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Legacy text column for replay JSON. New writes use <see cref="ReplayJsonGz"/>.</summary>
    public string ReplayJson { get; set; } = "";

    /// <summary>Gzipped UTF-8 of the replay JSON.</summary>
    public byte[]? ReplayJsonGz { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
