using System;

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
///   given grid position (rounded to the nearest cell), classifies (Missed
///   / Blocked / Cleared), mutates the board on Cleared, transitions the
///   timer + records the event. Verifier-facing.</item>
///   <item><see cref="RegisterViewTap"/> — live-path companion. Called by
///   the view adapter AFTER <see cref="BoardView.TryClearArrow"/> has already
///   classified + mutated the board. Trusts the caller-stated result;
///   performs only the state-update side of HandleTap (timer transition +
///   recorder event). Avoids re-mutating an already-mutated board on the
///   live path while keeping HandleTap a clean source-of-truth for the
///   verifier.</item>
/// </list>
///
/// <para>Both entry points share a private state-update routine so a
/// <see cref="HandleTap"/> call (verifier) and a corresponding
/// <see cref="RegisterViewTap"/> call (live) produce identical timer +
/// recorder + event-emission side effects for the same kind of tap.
/// "First-clear" and "last-clear" are not part of the result vocabulary —
/// they're derivable from <see cref="ClearedCount"/> / <see cref="IsCompleted"/>
/// and announced via <see cref="BoardCleared"/>.</para>
///
/// <para><b>Optional services</b>: a <see cref="ReplayRecorder"/> and
/// <see cref="GameTimer"/> are passed into the constructor. Both nullable
/// — the verifier instantiates ClassicRun without either when it only needs
/// to assert tap-by-tap results against final board state. Live mode passes
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
    /// so subsequent <see cref="ClearedCount"/> reads reflect total
    /// progression across sessions.
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
    /// Resolves a tap at the given float grid position against the current
    /// board state. Mutates the board on Cleared. Returns the result that
    /// actually applies — does NOT trust caller-stated outcome.
    /// Server-side verifier compares the returned result to the result on
    /// the recorded event.
    /// </summary>
    /// <param name="gridX">Float grid X (1 unit per cell). Run rounds to the nearest cell internally.</param>
    /// <param name="gridY">Float grid Y (1 unit per cell). Run rounds to the nearest cell internally.</param>
    /// <param name="wallTimeSeconds">Unix wall-clock seconds at the moment of the tap. Used by <see cref="GameTimer"/> for phase timestamps.</param>
    public TapResult HandleTap(float gridX, float gridY, double wallTimeSeconds)
    {
        if (_completed)
            return TapResult.Missed;

        var cell = new Cell(
            (int)Math.Round(gridX, MidpointRounding.AwayFromZero),
            (int)Math.Round(gridY, MidpointRounding.AwayFromZero)
        );
        if (!_board.Contains(cell))
            return TapResult.Missed;

        Arrow arrow = _board.GetArrowAt(cell);
        if (arrow == null)
        {
            ApplyState(TapResult.Missed, gridX, gridY, wallTimeSeconds);
            return TapResult.Missed;
        }

        if (!_board.IsClearable(arrow))
        {
            ApplyState(TapResult.Blocked, gridX, gridY, wallTimeSeconds);
            return TapResult.Blocked;
        }

        // Cleared — mutate board, then apply state. Reading
        // _board.Arrows.Count after RemoveArrow lets ApplyState detect the
        // last-arrow case for BoardCleared / timer.Finish without an extra
        // parameter.
        _board.RemoveArrow(arrow);
        _clearedCount++;
        ApplyState(TapResult.Cleared, gridX, gridY, wallTimeSeconds);
        return TapResult.Cleared;
    }

    // ---- Live-path entry --------------------------------------------------

    /// <summary>
    /// Live-path companion to <see cref="HandleTap"/>. Called by the view
    /// adapter after <see cref="BoardView.TryClearArrow"/> has already done
    /// its own classification + board mutation. Trusts <paramref name="result"/>
    /// and applies only the state-update side (timer + recorder + events) so
    /// the run-internal cleared counter, timer phase, and recorded events
    /// stay in sync with view-side mutation without double-mutating the
    /// board.
    /// </summary>
    /// <remarks>
    /// For <see cref="TapResult.Cleared"/> the caller is responsible for
    /// having already mutated the board (the view did this). The run reads
    /// the result verbatim and increments its own <see cref="ClearedCount"/>;
    /// do not call this for a result that the view didn't actually realize.
    /// </remarks>
    public void RegisterViewTap(TapResult result, float gridX, float gridY, double wallTimeSeconds)
    {
        if (_completed)
            return;
        if (result == TapResult.Cleared)
            _clearedCount++;
        ApplyState(result, gridX, gridY, wallTimeSeconds);
    }

    // ---- Internal: state update ------------------------------------------

    /// <summary>
    /// Shared timer + recorder + event-emission code path. Both
    /// <see cref="HandleTap"/> (verifier) and <see cref="RegisterViewTap"/>
    /// (live) funnel here so identical results produce identical state
    /// transitions regardless of entry point.
    /// </summary>
    private void ApplyState(TapResult result, float gridX, float gridY, double wallTime)
    {
        // Inspection ends on the first non-missed tap (Blocked counts —
        // engaging with the puzzle, even unsuccessfully, is enough). Missed
        // taps don't count: they're noise that shouldn't penalize someone
        // still scanning the board.
        if (result != TapResult.Missed)
            TryEndInspection(wallTime);

        switch (result)
        {
            case TapResult.Missed:
                _recorder?.RecordMiss(gridX, gridY);
                break;

            case TapResult.Blocked:
                _recorder?.RecordReject(gridX, gridY);
                break;

            case TapResult.Cleared:
                _recorder?.RecordClear(gridX, gridY);
                if (_board.Arrows.Count == 0)
                {
                    _timer?.Finish(wallTime);
                    _completed = true;
                    BoardCleared?.Invoke();
                }
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
