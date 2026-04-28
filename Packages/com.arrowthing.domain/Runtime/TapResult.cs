/// <summary>
/// Outcome of a player tap inside a run, as resolved by
/// <see cref="ClassicRun.HandleTap"/> / <see cref="EndlessRun.HandleTap"/>.
/// Stored on replay events so the server-side verifier can flag a player
/// who claimed a clear on a cell that simulation says was empty (or
/// blocked) at that moment.
///
/// <para>"First" / "last" distinctions live on the run's own counters
/// (<see cref="ClassicRun.ClearedCount"/>, <see cref="ClassicRun.IsCompleted"/>)
/// and on the <c>BoardCleared</c> event — they're not part of the tap
/// kind because the kind is meant to be a small, mode-symmetric domain
/// vocabulary that classifies the tap itself, not the run's progression.</para>
/// </summary>
public enum TapResult
{
    /// <summary>Tap hit an empty cell (or out of bounds). No arrow involved.</summary>
    Missed = 0,

    /// <summary>Arrow was tapped but is currently blocked by dependencies.</summary>
    Blocked = 1,

    /// <summary>Arrow was cleared. Removed from the board, recorded on the replay.</summary>
    Cleared = 2,
}
