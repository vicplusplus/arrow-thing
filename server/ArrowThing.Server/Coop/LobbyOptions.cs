namespace ArrowThing.Server.Coop;

/// <summary>
/// Config record for default lobby parameters. Production defaults to the
/// roadmap-mandated 200×200 board with max arrow length 40. Tests override
/// to a smaller size in <c>TestFactory</c> so the generation worker
/// finishes within a reasonable test timeout.
/// </summary>
public class LobbyOptions
{
    public int DefaultWidth { get; set; } = 200;
    public int DefaultHeight { get; set; } = 200;
    public int MaxArrowLength { get; set; } = 40;
}
