namespace ArrowThing.Server.Models;

public class Score
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Client-generated UUID for the game that produced this PB.</summary>
    public Guid GameId { get; set; }

    public int Seed { get; set; }
    public int BoardWidth { get; set; }
    public int BoardHeight { get; set; }
    public int MaxArrowLength { get; set; }

    /// <summary>Server-verified solve time in seconds.</summary>
    public double Time { get; set; }

    /// <summary>
    /// Legacy text column for replay JSON. Phase 4 introduces <see cref="ReplayJsonGz"/>
    /// which stores the same payload gzip-compressed in a bytea column.
    /// New writes set <see cref="ReplayJsonGz"/> only; this column is kept
    /// populated only for pre-Phase-4 rows that haven't been re-saved.
    /// Read path prefers <see cref="ReplayJsonGz"/> when present.
    /// </summary>
    public string ReplayJson { get; set; } = "";

    /// <summary>
    /// Phase 4: gzipped UTF-8 of the replay JSON. Top-50 scores include the
    /// boardSnapshot field (gzip-compressed, base64-encoded inside the JSON);
    /// non-top-50 have it stripped before gzipping. Null on pre-Phase-4 rows
    /// until they're backfilled or re-saved.
    /// </summary>
    public byte[]? ReplayJsonGz { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
