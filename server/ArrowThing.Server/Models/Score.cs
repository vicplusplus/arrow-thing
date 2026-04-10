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
    /// Full replay JSON. Top-50 scores include the boardSnapshot field
    /// (gzip-compressed, base64-encoded); non-top-50 have it stripped.
    /// </summary>
    public string ReplayJson { get; set; } = "";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
