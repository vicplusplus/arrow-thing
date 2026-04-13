namespace ArrowThing.Server.Models;

public enum LobbyEventType : short
{
    PlayerJoined = 0,
    ClearAccepted = 1,
    ClearRejectedDep = 2,
    LobbyCompleted = 3,
}

public class LobbyEvent
{
    public Guid LobbyId { get; set; }
    public long Seq { get; set; }
    public LobbyEventType Type { get; set; }
    public Guid? UserId { get; set; }
    public int? ArrowIndex { get; set; }
    public float? TapX { get; set; }
    public float? TapY { get; set; }
    public DateTime CreatedAt { get; set; }
}
