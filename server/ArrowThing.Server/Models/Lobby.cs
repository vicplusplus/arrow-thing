namespace ArrowThing.Server.Models;

public enum LobbyStatus : short
{
    Generating = 0,
    Active = 1,
    Completed = 2,
    GenerationFailed = 3,
    Deleted = 4,
}

public class Lobby
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public int Width { get; set; } = 200;
    public int Height { get; set; } = 200;
    public long Seed { get; set; }
    public int MaxArrowLength { get; set; } = 40;
    public LobbyStatus Status { get; set; } = LobbyStatus.Generating;
    public DateTime CreatedAt { get; set; }
    public DateTime? GeneratedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public DateTime? SnapshotStrippedAt { get; set; }
}
