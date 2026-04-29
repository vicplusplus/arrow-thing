using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// View adapter for endless mode. The actual game loop — sim-time clock,
/// push schedule, garbage meter, commit pipeline, clear handling, score
/// accumulation — lives on <see cref="EndlessRun"/> in the domain layer
/// (Unity-agnostic, server-replayable). This MonoBehaviour:
///
///   - Constructs an <see cref="EndlessRun"/> in <see cref="Initialize"/>.
///   - Forwards <c>Time.deltaTime</c> to <see cref="EndlessRun.Advance"/>
///     each Update.
///   - Subscribes to run events to drive view side effects: spawn arrow
///     views in pending mode, exit pending on commit, rebuild the meter
///     UI, toggle the danger tint, run the topout result-delay coroutine.
///   - Forwards player taps (already validated by <see cref="BoardView.TryClearArrow"/>)
///     into <see cref="EndlessRun.HandleTap"/> via the registered arrow-
///     remover hook on the board view.
///
/// The same <see cref="EndlessRun"/> class is what the (future) replay
/// verifier instantiates — it walks recorded tap events, calls
/// <see cref="EndlessRun.Advance"/> with sim-time deltas and
/// <see cref="EndlessRun.HandleTap"/> with cell coords, then compares
/// final state to the player's claimed stats. No bespoke simulator.
/// </summary>
public sealed class EndlessModeController : MonoBehaviour
{
    /// <summary>
    /// Pause between topout detection and the result screen appearing. Lets
    /// the player see the final saturated board state instead of being
    /// dropped straight into a modal. View-only; not part of EndlessTuning
    /// because the server simulator doesn't care.
    /// </summary>
    [SerializeField]
    private float topoutResultDelaySeconds = 2f;

    // ---- State -------------------------------------------------------------

    private EndlessRun _run;
    private BoardView _boardView;
    private List<Color> _palette;

    // HUD-bound elements (resolved during Initialize; null-tolerant).
    private VisualElement _meterContainer;
    private Label _clearsLabel;

    // Camera whose background color we modulate for the danger tint.
    private Camera _camera;
    private Color _cameraBaseColor;

    // Tint base color (RGB only; alpha is computed from shortfall presence).
    private static readonly Color DangerColorRgb = new(0.85f, 0.20f, 0.20f);
    private const float DangerMaxAlpha = 0.18f;
    private const float DangerPulseHz = 1.5f;
    private const float DangerPulseAmp = 0.07f;

    // Meter UI styling (inline since the children are built in C#).
    private static readonly Color MeterShortfallColor = new(0.95f, 0.20f, 0.20f);

    /// <summary>Pixel height per garbage point. Combo bar height = points × this.</summary>
    private const float MeterPointPixelHeight = 12f;

    // ---- Read-only facades over the run (consumed by EndlessMode) ---------

    public int ClearCount => _run?.ClearCount ?? 0;
    public int LongestCombo => _run?.LongestCombo ?? 0;
    public float SimTime => _run?.SimTime ?? 0f;
    public float RunDurationSeconds => _run?.RunDurationSeconds ?? 0f;
    public bool RunEnded => _run != null && !_run.IsActive;

    // ---- Events ------------------------------------------------------------

    /// <summary>Raised the moment topout is detected. Consumers disable input + HUD.</summary>
    public event Action ToppedOut;

    /// <summary>Raised after <see cref="topoutResultDelaySeconds"/> following <see cref="ToppedOut"/>.</summary>
    public event Action ResultReady;

    // ---- Lifecycle ---------------------------------------------------------

    public void Initialize(
        Board board,
        BoardView boardView,
        int spawnSeed,
        VisualElement hudRoot = null,
        Camera camera = null
    )
    {
        if (board == null)
            throw new ArgumentNullException(nameof(board));
        if (boardView == null)
            throw new ArgumentNullException(nameof(boardView));

        _boardView = boardView;
        _palette = ThemeManager.Current?.arrowPalette;

        _camera = camera;
        if (_camera != null)
            _cameraBaseColor = _camera.backgroundColor;

        // Endless-specific HUD elements live in EndlessHud.uxml (cloned into
        // the HUD root by EndlessMode.RunSetup). They do not exist on
        // classic / co-op HUDs.
        _meterContainer = hudRoot?.Q<VisualElement>("endless-meter");
        if (_meterContainer == null)
            Debug.LogWarning(
                "[EndlessModeController] endless-meter element not found in HUD root — was EndlessHud.uxml cloned?"
            );
        else
            _meterContainer.Clear();

        _clearsLabel = hudRoot?.Q<Label>("endless-clears-label");
        if (_clearsLabel != null)
            _clearsLabel.text = "0";

        // Construct the run. Tuning version is the current one shipped by
        // the client; replay verifier reads the version from the replay
        // payload to pick the right historical tuning.
        var tuning = EndlessTuning.ForVersion(EndlessTuning.CurrentVersion);
        int paletteCount = _palette?.Count ?? 0;
        _run = new EndlessRun(board, spawnSeed, paletteCount, tuning);

        // View reactions to run events. Subscribed BEFORE Begin() so the
        // initial-push spawn events reach OnPendingSpawned and produce
        // ArrowViews — otherwise the first wave of arrows would commit
        // into the live board without views, and tapping them would warn
        // "no view for arrow@(x,y)" and refuse to clear.
        _run.PendingSpawned += OnPendingSpawned;
        _run.PendingCommitted += OnPendingCommitted;
        _run.MeterChanged += RebuildMeterUI;
        _run.ToppedOut += OnRunToppedOut;
        _run.Begin();

        // Initial meter render reflects the seeded combo + first immediate push.
        RebuildMeterUI();
    }

    /// <summary>
    /// Hook for <see cref="BoardView.SetArrowRemover"/>. Called when the
    /// player taps a clearable arrow — BoardView has already validated
    /// clearability and is about to play the pull-out animation. We forward
    /// the cell to <see cref="EndlessRun.HandleTap"/>, which mutates the
    /// board (removes the arrow + advances spawn-side <c>NativeGenerationState</c>),
    /// updates stats, and may fire immediate-mode shortfall placements.
    /// </summary>
    public void HandleRealArrowCleared(Arrow arrow)
    {
        if (_run == null || arrow == null)
            return;
        // Cell→grid conversion is identity (each cell is 1×1 grid unit).
        _run.HandleTap(arrow.HeadCell.X, arrow.HeadCell.Y);
    }

    // ---- Per-frame ---------------------------------------------------------

    private void Update()
    {
        if (_run == null || !_run.IsActive)
            return;

        _run.Advance(Time.deltaTime);

        // View updates derived from run state.
        UpdateDangerTint();
        if (_clearsLabel != null)
            _clearsLabel.text = _run.ClearCount.ToString();
    }

    // ---- Run event handlers (view side effects) ----------------------------

    private void OnPendingSpawned(PendingArrow pending, int colorIndex)
    {
        _boardView.AddArrowView(pending.Arrow);
        ArrowView view = _boardView.GetArrowView(pending.Arrow);
        if (view != null)
        {
            view.EnterPendingMode();
            _boardView.SetArrowColor(pending.Arrow, ColorForIndex(colorIndex));
        }
    }

    private void OnPendingCommitted(PendingArrow pending)
    {
        ArrowView view = _boardView.GetArrowView(pending.Arrow);
        if (view != null)
            view.ExitPendingMode();
        // No ApplyColoring — the per-combo color stamped at spawn time
        // sticks through pending → committed for visual continuity.
    }

    private void OnRunToppedOut(float simTime)
    {
        ToppedOut?.Invoke();
        StartCoroutine(ResultDelayCoroutine());
    }

    private System.Collections.IEnumerator ResultDelayCoroutine()
    {
        // Clear any active danger tint — the flashing belongs to live play,
        // not the post-topout review pause.
        ClearDangerTint();

        yield return new WaitForSeconds(topoutResultDelaySeconds);
        ResultReady?.Invoke();
    }

    // ---- Danger tint -------------------------------------------------------

    private void UpdateDangerTint()
    {
        if (_camera == null)
            return;

        // Run is over — restore the original background color regardless of
        // ordering quirks that may have called us after EndRun.
        if (_run == null || !_run.IsActive)
        {
            _camera.backgroundColor = _cameraBaseColor;
            return;
        }

        // Tint intensity is binary on shortfall presence. Pulses to make it
        // unmissable while uncommitted arrows are queued. Pulse uses sim
        // time so a paused tab doesn't drift the phase off the gameplay.
        float t = _run.ActiveShortfall > 0 ? 1f : 0f;
        float pulse = Mathf.Sin(_run.SimTime * 2f * Mathf.PI * DangerPulseHz);
        float strength = (t * DangerMaxAlpha) + (t * DangerPulseAmp * pulse);
        if (strength < 0f)
            strength = 0f;
        if (strength > 1f)
            strength = 1f;

        _camera.backgroundColor = Color.Lerp(_cameraBaseColor, DangerColorRgb, strength);
    }

    private void ClearDangerTint()
    {
        if (_camera == null)
            return;
        _camera.backgroundColor = _cameraBaseColor;
    }

    // ---- Meter UI ----------------------------------------------------------

    private void RebuildMeterUI()
    {
        if (_meterContainer == null || _run == null)
            return;

        _meterContainer.Clear();

        // Bottom-justified flex (column + flex-end via USS). Newest combo
        // sits at the top of the stack; oldest pops from the bottom. Bar
        // colors come from the palette index the run picked deterministically.
        var meter = _run.Meter;
        for (int i = meter.Count - 1; i >= 0; i--)
        {
            var bar = new VisualElement();
            bar.AddToClassList("endless-meter-bar");
            bar.style.backgroundColor = ColorForIndex(meter[i].ColorIndex);
            bar.style.height = meter[i].Size * MeterPointPixelHeight;
            _meterContainer.Add(bar);
        }

        if (_run.ActiveShortfall > 0)
        {
            var bar = new VisualElement();
            bar.AddToClassList("endless-meter-bar");
            bar.AddToClassList("endless-meter-bar--shortfall");
            bar.style.backgroundColor = MeterShortfallColor;
            bar.style.height = _run.ActiveShortfall * MeterPointPixelHeight;
            _meterContainer.Add(bar);
        }
    }

    private Color ColorForIndex(int idx)
    {
        if (_palette == null || _palette.Count == 0)
            return new Color(0.78f, 0.78f, 0.82f); // neutral gray fallback
        if (idx < 0 || idx >= _palette.Count)
            idx = 0;
        return _palette[idx];
    }
}
