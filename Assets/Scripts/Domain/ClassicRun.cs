using System;

/// <summary>
/// Outcome of a player tap inside a classic run, as resolved by
/// <see cref="ClassicRun.HandleTap"/>. Stored on replay events so the
/// server-side verifier can flag a player who claimed a clear on a cell
/// that simulation says was empty (or blocked) at that wall time.
/// </summary>
public enum ClassicTapKind
{
    /// <summary>Tap hit an empty cell. No arrow involved.</summary>
    Missed = 0,

    /// <summary>Arrow was tapped but is currently blocked by dependencies.</summary>
    Blocked = 1,

    /// <summary>Arrow was cleared. Not the first or last on the board.</summary>
    Cleared = 2,

    /// <summary>Arrow was cleared. It was the first arrow cleared this run (inspection ends).</summary>
    ClearedFirst = 3,

    /// <summary>Arrow was cleared. It was the last arrow on the board (win condition).</summary>
    ClearedLast = 4,
}

/// <summary>
/// Pure-C# classic-mode game loop. Owns the fixed-board lifecycle: tap
/// resolution, board mutation on clear, inspection→solve timer transition,
/// replay-event recording, board-cleared (victory) detection.
///
/// Unity-agnostic so the same code drives both the live game (via the
/// <see cref="ClassicMode"/> view adapter) and the future server-side
/// replay verifier.
///
/// <para><b>Two driving entry points</b>:</para>
/// <list type="bullet">
///   <item><see cref="HandleTap"/> — full path. Looks up the arrow at the
///   given cell, classifies (Missed / Blocked / Cleared*), mutates the
///   board on Cleared, transitions the timer + records the event. Verifier
///   uses this exclusively: it walks recorded events, calls HandleTap with
///   the recorded cell + world pos + wall time, and asserts the returned
///   kind matches the kind on the recorded event.</item>
///   <item><see cref="RegisterViewTap"/> — live-path companion. Called by
///   the view adapter AFTER <see cref="BoardView.TryClearArrow"/> has already
///   classified + mutated the board. Trusts the caller-stated kind; performs
///   only the state-update side of HandleTap (timer transition + recorder
///   event). Avoids re-mutating an already-mutated board on the live path
///   while keeping HandleTap a clean source-of-truth for the verifier.</item>
/// </list>
///
/// <para>Both entry points share a private state-update routine so a
/// HandleTap call (verifier) and a corresponding RegisterViewTap call
/// (live) produce identical timer + recorder + event-emission side effects
/// for the same kind of tap. Determinism contract: identical sequence of
/// {kind, cell, wallTime} produces identical {ClearedCount, IsCompleted,
/// timer.SolveElapsed at completion}.</para>
///
/// <para><b>Optional services</b>: a <see cref="ReplayRecorder"/> and
/// <see cref="GameTimer"/> are passed into the constructor. Both nullable
/// — the verifier instantiates ClassicRun without either when it only needs
/// to assert tap-by-tap kinds against final board state. Live mode passes
/// both so the run drives recorder + timer side effects in lock-step with
/// state mutation.</para>
/// </summary>
public sealed class ClassicRun
{
    private readonly Board _board;
    private readonly ReplayRecorder _recorder;
    private readonly GameTimer _timer;

    private int _clearedCount;
    private bool _completed;

    // ---- Read-only state (consumed by view + verifier) --------------------

    /// <summary>The shared board the run mutates as the player clears arrows.</summary>
    public Board Board => _board;

    /// <summary>Number of arrows cleared so far this run.</summary>
    public int ClearedCount => _clearedCount;

    /// <summary>True once the last arrow has been cleared (board fully solved).</summary>
    public bool IsCompleted => _completed;

    /// <summary>The recorder this run appends events to. Null when running headless (verifier).</summary>
    public ReplayRecorder Recorder => _recorder;

    /// <summary>The timer this run transitions on first non-missed tap. Null when running headless.</summary>
    public GameTimer Timer => _timer;

    // ---- Events (view-only; verifier ignores) -----------------------------

    /// <summary>Fired the first time a non-missed tap arrives. Mode subscribes for any inspection-end UI.</summary>
    public event Action InspectionEnded;

    /// <summary>Fired immediately after the last arrow's clear (before any view animation). Mode wires victory.</summary>
    public event Action BoardCleared;

    // ---- Construction -----------------------------------------------------

    public ClassicRun(Board board, ReplayRecorder recorder = null, GameTimer timer = null)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _recorder = recorder;
        _timer = timer;
    }

    /// <summary>
    /// Resume-aware constructor. Called when restoring from a save: the
    /// supplied <paramref name="alreadyCleared"/> seeds the cleared counter
    /// so wasFirst/wasLast computation accounts for arrows cleared in the
    /// prior session.
    /// </summary>
    public ClassicRun(Board board, ReplayRecorder recorder, GameTimer timer, int alreadyCleared)
        : this(board, recorder, timer)
    {
        if (alreadyCleared < 0)
            throw new ArgumentOutOfRangeException(nameof(alreadyCleared));
        _clearedCount = alreadyCleared;
    }

    // ---- Verifier-facing entry --------------------------------------------

    /// <summary>
    /// Resolves a tap at <paramref name="cellX"/>,<paramref name="cellY"/>
    /// against the current board state. Mutates the board on Cleared.
    /// Returns the kind that actually applies — does NOT trust caller-stated
    /// outcome. Server-side verifier compares the returned kind to the kind
    /// on the recorded event.
    /// </summary>
    public ClassicTapKind HandleTap(
        int cellX,
        int cellY,
        double wallTimeSeconds,
        float worldX,
        float worldY
    )
    {
        if (_completed)
            return ClassicTapKind.Missed;

        var cell = new Cell(cellX, cellY);
        if (!_board.Contains(cell))
            return ClassicTapKind.Missed;

        Arrow arrow = _board.GetArrowAt(cell);
        if (arrow == null)
        {
            ApplyState(ClassicTapKind.Missed, worldX, worldY, wallTimeSeconds);
            return ClassicTapKind.Missed;
        }

        if (!_board.IsClearable(arrow))
        {
            ApplyState(ClassicTapKind.Blocked, worldX, worldY, wallTimeSeconds);
            return ClassicTapKind.Blocked;
        }

        // Cleared — mutate board, then classify + apply state. Classifying
        // after mutation lets ApplyState read the post-removal Arrows.Count
        // for wasLast detection without an extra parameter.
        _board.RemoveArrow(arrow);
        _clearedCount++;
        ClassicTapKind kind = ClassifyClearedPostMutation();
        ApplyState(kind, worldX, worldY, wallTimeSeconds);
        return kind;
    }

    // ---- Live-path entry --------------------------------------------------

    /// <summary>
    /// Live-path companion to <see cref="HandleTap"/>. Called by the view
    /// adapter after <see cref="BoardView.TryClearArrow"/> has already done
    /// its own classification + board mutation. Trusts <paramref name="kind"/>,
    /// applies only the state-update side (timer + recorder + events) so the
    /// run-internal cleared counter, timer phase, and recorded events stay in
    /// sync with view-side mutation without double-mutating the board.
    /// </summary>
    /// <remarks>
    /// For Cleared variants the caller is responsible for having already
    /// mutated the board (the view did this). The run reads the kind verbatim
    /// and increments its own <see cref="ClearedCount"/>; do not call this for
    /// a kind that the view didn't actually realize.
    /// </remarks>
    public void RegisterViewTap(
        ClassicTapKind kind,
        int cellX,
        int cellY,
        double wallTimeSeconds,
        float worldX,
        float worldY
    )
    {
        if (_completed)
            return;

        switch (kind)
        {
            case ClassicTapKind.Cleared:
            case ClassicTapKind.ClearedFirst:
            case ClassicTapKind.ClearedLast:
                _clearedCount++;
                break;
        }

        ApplyState(kind, worldX, worldY, wallTimeSeconds);
    }

    // ---- Internal: classification + state update --------------------------

    /// <summary>
    /// Determines whether the just-mutated cleared tap was first / last /
    /// neither, based on the run's own counter and the post-mutation board
    /// occupancy. Called from <see cref="HandleTap"/> after RemoveArrow.
    /// </summary>
    private ClassicTapKind ClassifyClearedPostMutation()
    {
        bool wasFirst = _clearedCount == 1;
        bool wasLast = _board.Arrows.Count == 0;
        return wasLast ? ClassicTapKind.ClearedLast
            : wasFirst ? ClassicTapKind.ClearedFirst
            : ClassicTapKind.Cleared;
    }

    /// <summary>
    /// Shared timer + recorder + event-emission code path. Both
    /// <see cref="HandleTap"/> (verifier) and <see cref="RegisterViewTap"/>
    /// (live) funnel here so identical kinds produce identical state
    /// transitions regardless of entry point.
    /// </summary>
    private void ApplyState(ClassicTapKind kind, float worldX, float worldY, double wallTime)
    {
        // Inspection ends on the first non-missed tap (Blocked counts —
        // engaging with the puzzle, even unsuccessfully, is enough). Missed
        // taps don't count: they're noise that shouldn't penalize someone
        // still scanning the board.
        if (kind != ClassicTapKind.Missed)
            TryEndInspection(wallTime);

        switch (kind)
        {
            case ClassicTapKind.Missed:
                _recorder?.RecordMiss(worldX, worldY);
                break;

            case ClassicTapKind.Blocked:
                _recorder?.RecordReject(worldX, worldY);
                break;

            case ClassicTapKind.Cleared:
            case ClassicTapKind.ClearedFirst:
                _recorder?.RecordClear(worldX, worldY);
                break;

            case ClassicTapKind.ClearedLast:
                _recorder?.RecordClear(worldX, worldY);
                _timer?.Finish(wallTime);
                _completed = true;
                BoardCleared?.Invoke();
                break;
        }
    }

    private void TryEndInspection(double wallTimeSeconds)
    {
        if (_timer == null)
            return;
        if (_timer.IsSolving)
            return;
        if (_timer.CurrentPhase == GameTimer.Phase.Finished)
            return;
        _timer.StartSolve(wallTimeSeconds);
        _recorder?.RecordStartSolve();
        InspectionEnded?.Invoke();
    }
}
