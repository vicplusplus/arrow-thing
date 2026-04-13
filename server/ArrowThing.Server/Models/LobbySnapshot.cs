namespace ArrowThing.Server.Models;

public enum LobbySnapshotFormat : short
{
    BinaryV1 = 0,
}

public class LobbySnapshot
{
    public Guid LobbyId { get; set; }
    public LobbySnapshotFormat Format { get; set; } = LobbySnapshotFormat.BinaryV1;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime UpdatedAt { get; set; }
}
