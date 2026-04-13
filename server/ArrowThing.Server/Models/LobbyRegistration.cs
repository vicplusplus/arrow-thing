namespace ArrowThing.Server.Models;

public class LobbyRegistration
{
    public Guid LobbyId { get; set; }
    public Guid UserId { get; set; }
    public string DisplayNameAtJoin { get; set; } = "";
    public string ColorAtJoin { get; set; } = "";
    public int ClearCount { get; set; }
    public long AccumulatedMillis { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? FirstClearAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
}
