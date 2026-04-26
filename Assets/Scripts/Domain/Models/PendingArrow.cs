/// <summary>
/// A ghost arrow generated into the endless-mode spawn pipeline but not yet
/// committed to the Board as a real arrow. Visible to the player as a warning
/// of incoming pressure; does not participate in real-arrow clearability.
///
/// Participation in the generation state is asymmetric:
///   - In <see cref="NativeGenerationState"/>: placed as if real (so subsequent
///     ghost spawns cycle-check against it, preventing commit-time loops).
///   - In <see cref="Board"/>: NOT in <c>_arrows</c> / <c>_occupancy</c> /
///     dependency graph. Real arrows' rays pass through ghost cells.
///
/// At commit time, the session adds the underlying <see cref="Arrow"/> to the
/// Board via <see cref="Board.AddArrow"/>, flipping the ghost's dormant dep
/// relationships live. The generation index assigned at spawn time is preserved
/// across commit so the native state does not need reshuffling.
/// </summary>
public sealed class PendingArrow
{
    /// <summary>The underlying arrow. Same object committed to the Board.</summary>
    public Arrow Arrow { get; }

    /// <summary>Generation index in the session's native state. Stable across commit.</summary>
    public int GenIndex { get; }

    /// <summary>Absolute time (engine-agnostic seconds) at which this ghost should commit.</summary>
    public float CommitAt { get; }

    /// <summary>
    /// Number of perpendicular forward dependencies this arrow has when
    /// committed — i.e. how many crossing arrows already lie in its forward
    /// ray. Endless mode uses this as a difficulty weight for the garbage
    /// meter (collinear deps are trivial chains; perpendicular deps require
    /// the player to plan around cross-traffic).
    /// </summary>
    public int PerpForwardDeps { get; }

    public PendingArrow(Arrow arrow, int genIndex, float commitAt, int perpForwardDeps)
    {
        Arrow = arrow ?? throw new System.ArgumentNullException(nameof(arrow));
        GenIndex = genIndex;
        CommitAt = commitAt;
        PerpForwardDeps = perpForwardDeps;
    }
}
