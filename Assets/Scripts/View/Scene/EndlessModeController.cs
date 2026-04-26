using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Runs the singleplayer endless-mode loop on a combo-queue garbage-meter
/// model:
///
///   1. <b>Push tick</b> (every <see cref="CurrentPushIntervalSeconds"/>): a new combo
///      of size C (computed from board fill + total clears) is appended to the
///      back of <see cref="_meter"/>. The oldest combo is then popped and
///      "sent to pending" — the generator tries to spawn C pending arrows.
///   2. <b>Shortfall</b>: arrows the generator couldn't fit (no room) become
///      "uncommitted" and trigger <i>immediate mode</i>: every player clear
///      retries placement until the shortfall is gone.
///   3. <b>Topout</b>: at the next push tick, if the previous combo still has
///      shortfall, the player ran out of time → end of run.
///
/// View hooks: a vertical meter UI on the left of the HUD shows queued combo
/// segments + a shortfall bar at the bottom. A full-screen tint pulses red
/// while shortfall > 0.
/// </summary>
public sealed class EndlessModeController : MonoBehaviour
{
    // ---- Tuning (provisional; SerializeFields tunable in the inspector) ---

    /// <summary>Preview phase duration: how long a pending arrow is visible before committing.</summary>
    [SerializeField]
    private float previewDurationSeconds = 3f;

    /// <summary>
    /// Max arrow length the spawn generator may produce. Smaller values mean
    /// uniform-length sampling produces a useful range (e.g. 6 → arrows of
    /// length 2–6, average ~4). Higher = more variety but fewer arrows fit
    /// in tight spaces. Independent of <c>GameSettings.MaxArrowLength</c>
    /// (which is classic-mode-specific).
    /// </summary>
    [SerializeField]
    private int endlessMaxArrowLength = 6;

    /// <summary>Garbage cost charged for any arrow regardless of complexity.</summary>
    [SerializeField]
    private int weightPerArrow = 1;

    /// <summary>
    /// Garbage cost added per perpendicular forward dep at placement time.
    /// Cross-traffic is the interesting puzzle — collinear chains are
    /// trivial. Higher = combos resolve in fewer (harder) arrows.
    /// </summary>
    [SerializeField]
    private int weightPerPerpDep = 2;

    /// <summary>
    /// Number of candidate placements sampled per arrow spawn (the
    /// highest-scoring is kept). Constant — arrow QUALITY (perp-dep
    /// density) stays stable across the run. Difficulty escalation
    /// happens entirely via push cadence (see <see cref="pushIntervalAtStartSeconds"/>).
    /// </summary>
    [SerializeField]
    private int spawnCandidates = 6;

    /// <summary>Sweet-spot arrow length the scorer prefers (no penalty at this length).</summary>
    [SerializeField]
    private int scoreIdealLength = 4;

    /// <summary>Per-cell penalty for arrows shorter or longer than the ideal.</summary>
    [SerializeField]
    private int scoreLengthPenalty = 2;

    /// <summary>Per-perp-dep bonus in the score function. The dominant signal — favor cross-traffic.</summary>
    [SerializeField]
    private int scorePerpDepBonus = 5;

    /// <summary>
    /// Push interval at run start. Combos arrive this far apart at clear=0
    /// and the gap shrinks toward <see cref="pushIntervalMinSeconds"/> as
    /// the player progresses (see <see cref="pushIntervalStepDownPerTier"/>).
    /// Difficulty escalation happens via cadence, not combo size.
    /// </summary>
    [SerializeField]
    private float pushIntervalAtStartSeconds = 2.0f;

    /// <summary>
    /// Hard floor on push interval. Past this, no further speedup — prevents
    /// the late game from becoming literally unwinnable.
    /// </summary>
    [SerializeField]
    private float pushIntervalMinSeconds = 0.7f;

    /// <summary>
    /// Per-tier push-interval reduction in seconds. With default 0.1s and
    /// the default tier curve, interval shrinks ~0.1s per tier:
    ///   tier 0 (clear 0)    → 2.0s
    ///   tier 2 (clear 25)   → 1.8s
    ///   tier 5 (clear 87)   → 1.5s
    ///   tier 10 (clear 230) → 1.0s
    ///   tier 13 (clear 365) → 0.7s (floor)
    /// </summary>
    [SerializeField]
    private float pushIntervalStepDownPerTier = 0.1f;

    /// <summary>
    /// Combo size in <i>garbage points</i> (each arrow contributes its
    /// cell-count). Constant baseline — only the occupancy catch-up bonus
    /// modulates it. Difficulty escalation comes from push cadence, not
    /// combo size.
    ///
    /// Roughly 1 point ≈ 1 cell of arrow. With avg arrow length ~4, a combo
    /// of 12 points = ~3 arrows.
    /// </summary>
    [SerializeField]
    private int comboBaseRate = 9;

    /// <summary>
    /// Clear count for the first difficulty tier. Subsequent tiers follow
    /// the power-law curve <c>tier = floor((clears / firstTier)^power)</c>.
    /// Tier feeds into push-interval speedup (see
    /// <see cref="pushIntervalStepDownPerTier"/>).
    /// </summary>
    [SerializeField]
    private int clearsForFirstTier = 10;

    /// <summary>
    /// Power-law exponent for the difficulty tier ramp.
    /// <c>tier = floor((clears / firstTier)^power)</c>. 0.5 = sqrt (gentle);
    /// 1.0 = linear (steep); 0.7+ = perceptibly rising late-game. Higher
    /// values shorten run length.
    /// </summary>
    [SerializeField, Range(0.4f, 1f)]
    private float difficultyTierPower = 0.78f;

    /// <summary>
    /// Target board occupancy the adaptive bonus ramps down to. Below this,
    /// combos get a catch-up bonus; at and above this, the bonus is zero
    /// (no negative penalty — we don't want full boards to feel forgiving).
    /// </summary>
    [SerializeField, Range(0f, 1f)]
    private float targetOccupancy = 0.5f;

    /// <summary>
    /// Peak magnitude of the occupancy catch-up bonus. At empty (occupancy
    /// 0) the bonus is +scale; the bonus rolls off smoothly via a quarter-
    /// cosine curve and reaches 0 at <see cref="targetOccupancy"/>.
    /// Above target, the bonus stays clamped at 0.
    /// </summary>
    [SerializeField]
    private float occupancyAdaptiveScale = 22f;

    /// <summary>
    /// Combo timer: if no clear lands within this window after the last one,
    /// the current combo counter resets. Independent of the garbage-combo
    /// system; this is the player's clear-streak display.
    /// </summary>
    [SerializeField]
    private float comboTimerSeconds = 1f;

    /// <summary>
    /// Pause between topout detection and the result screen appearing. Lets
    /// the player see the final saturated board state instead of being
    /// dropped straight into a modal.
    /// </summary>
    [SerializeField]
    private float topoutResultDelaySeconds = 2f;

    // ---- State -------------------------------------------------------------

    private EndlessBoardSession _session;
    private BoardView _boardView;
    private bool _active;

    // Garbage meter: FIFO queue of combos. _meter[0] = oldest = next to
    // be sent to pending at the next push tick. Each combo carries a
    // palette color index so its bar in the meter UI and the arrows it
    // spawns share the same color (visual identity from queue → board).
    private readonly struct MeterEntry
    {
        public readonly int Size;
        public readonly int ColorIndex;

        public MeterEntry(int size, int colorIndex)
        {
            Size = size;
            ColorIndex = colorIndex;
        }
    }

    private readonly List<MeterEntry> _meter = new();
    private MeterEntry _activeCombo;
    private int _lastColorIndex = -1;
    private int _activeShortfall;
    private float _pushTimer;

    // HUD-bound elements (resolved during Initialize; null-tolerant).
    private VisualElement _meterContainer;
    private Label _clearsLabel;

    // Camera whose background color we modulate for the danger tint. The
    // board is rendered by this camera; tinting its clear color paints the
    // area behind/around arrows without obscuring the arrows themselves.
    private Camera _camera;
    private Color _cameraBaseColor;

    // Tint base color (RGB only; alpha is computed from shortfall presence).
    private static readonly Color DangerColorRgb = new(0.85f, 0.20f, 0.20f);
    private const float DangerMaxAlpha = 0.18f;
    private const float DangerPulseHz = 1.5f;
    private const float DangerPulseAmp = 0.07f;

    // Meter UI styling (inline since the children are built in C#).
    private static readonly Color MeterQueuedColor = new(0.78f, 0.78f, 0.82f);
    private static readonly Color MeterShortfallColor = new(0.95f, 0.20f, 0.20f);

    /// <summary>Pixel height per garbage point. Combo bar height = points × this.</summary>
    private const float MeterPointPixelHeight = 12f;

    // Scoring (publicly read by EndlessMode for the result screen).
    public int ClearCount { get; private set; }
    public int LongestCombo { get; private set; }
    private int _currentCombo;
    private float _runStartTime;
    private float _runEndTime;

    // Combo timer: timestamp of the last real-arrow clear.
    private float _lastClearTime = -1f;

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
        _session = new EndlessBoardSession(board, endlessMaxArrowLength, spawnSeed);

        _camera = camera;
        if (_camera != null)
            _cameraBaseColor = _camera.backgroundColor;

        // Endless-specific HUD elements live in EndlessHud.uxml (cloned
        // into the HUD root by EndlessMode.RunSetup). They do not exist
        // on classic / co-op HUDs.
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

        ClearCount = 0;
        _currentCombo = 0;
        LongestCombo = 0;
        _meter.Clear();
        _lastColorIndex = -1;
        _activeShortfall = 0;
        _pushTimer = 0f;
        _runStartTime = Time.time;
        _active = true;

        // Seed the meter with one combo so the first push tick has something
        // to send to pending instead of an empty queue.
        _meter.Add(NewComboEntry());

        // Fire the first push immediately rather than waiting for the empty-
        // board push interval. Empty-board start (no initial fill) means the
        // player would otherwise stare at a blank board for the full interval
        // before any arrows appear.
        OnPushTick();
    }

    /// <summary>
    /// Call this when a real (committed) arrow is cleared by the player. Routes
    /// removal through the session AND, if there's outstanding shortfall, fires
    /// immediate-mode placement to chip away at it.
    /// </summary>
    public void HandleRealArrowCleared(Arrow arrow)
    {
        if (!_active || arrow == null)
            return;

        _session.ClearRealArrow(arrow);
        ClearCount++;
        _currentCombo++;
        if (_currentCombo > LongestCombo)
            LongestCombo = _currentCombo;
        _lastClearTime = Time.time;

        // Immediate mode: shortfall garbage now has a chance to fit. Try to
        // spawn arrows until we cover the shortfall (in points). Whatever
        // still doesn't fit waits for the next clear or the next push tick
        // (which will topout if it's still not zero).
        if (_activeShortfall > 0)
        {
            float commitAt = Time.time + previewDurationSeconds;
            while (_activeShortfall > 0)
            {
                if (!TrySpawnOnePending(commitAt, out int weight))
                    break;
                _activeShortfall -= weight;
            }
            if (_activeShortfall < 0)
                _activeShortfall = 0;
            RebuildMeterUI();
        }
    }

    private void BreakCombo()
    {
        _currentCombo = 0;
    }

    // ---- Push tick ---------------------------------------------------------

    private void OnPushTick()
    {
        // Topout: previous combo still has unfilled shortfall. The player
        // ran out of time to clear room.
        if (_activeShortfall > 0)
        {
            EndRun();
            return;
        }

        // Pop oldest from meter (= active combo's garbage points to send to
        // pending). Spawn arrows until we've placed >= combo points worth;
        // whatever doesn't fit becomes shortfall (carries the same combo
        // color so immediate-mode placements stay visually grouped).
        if (_meter.Count > 0)
        {
            _activeCombo = _meter[0];
            _meter.RemoveAt(0);
            int placed = SpawnComboPoints(_activeCombo.Size);
            int shortfall = _activeCombo.Size - placed;
            _activeShortfall = shortfall < 0 ? 0 : shortfall;
        }

        // Push a new combo to the back so there's always something queued.
        _meter.Add(NewComboEntry());

        RebuildMeterUI();
    }

    /// <summary>
    /// Spawns arrows until cumulative points (= sum of arrow lengths) reaches
    /// or exceeds <paramref name="comboPoints"/>. Returns the actual point
    /// total placed (may overshoot the request by up to one arrow's length,
    /// or fall short if the board ran out of room).
    /// </summary>
    private int SpawnComboPoints(int comboPoints)
    {
        float commitAt = Time.time + previewDurationSeconds;
        int placed = 0;
        while (placed < comboPoints)
        {
            if (!TrySpawnOnePending(commitAt, out int weight))
                break;
            placed += weight;
        }
        return placed;
    }

    /// <summary>
    /// Builds a fresh combo entry: random size from <see cref="ComputeComboSize"/>
    /// + a palette color index different from the previous combo's. Updates
    /// <see cref="_lastColorIndex"/> so consecutive combos never share color.
    /// </summary>
    private MeterEntry NewComboEntry()
    {
        int colorIdx = PickComboColorIndex();
        _lastColorIndex = colorIdx;
        return new MeterEntry(ComputeComboSize(), colorIdx);
    }

    /// <summary>
    /// Picks a random palette index that differs from <see cref="_lastColorIndex"/>.
    /// Standard "pick from N-1 then bump past the excluded index" trick.
    /// Falls back to 0 if the palette is empty/single-entry.
    /// </summary>
    private int PickComboColorIndex()
    {
        var palette = ActivePalette;
        if (palette == null || palette.Count == 0)
            return 0;
        if (palette.Count == 1 || _lastColorIndex < 0)
            return UnityEngine.Random.Range(0, palette.Count);
        int idx = UnityEngine.Random.Range(0, palette.Count - 1);
        if (idx >= _lastColorIndex)
            idx++;
        return idx;
    }

    /// <summary>Active palette (theme override or scene default). Null if neither resolved.</summary>
    private List<Color> ActivePalette =>
        ThemeManager.Current != null ? ThemeManager.Current.arrowPalette : null;

    /// <summary>Resolves a combo's color from its palette index, with a sane fallback.</summary>
    private Color ColorForCombo(MeterEntry combo)
    {
        var palette = ActivePalette;
        if (palette == null || palette.Count == 0)
            return MeterQueuedColor;
        int idx = combo.ColorIndex;
        if (idx < 0 || idx >= palette.Count)
            idx = 0;
        return palette[idx];
    }

    /// <summary>
    /// Score function passed to the multi-candidate generator. Higher = more
    /// preferred. Rewards cross-traffic (perp deps) heavily; penalizes
    /// length deviation from the ideal sweet spot.
    /// </summary>
    private int ScoreCandidate(int cellCount, int perpDeps)
    {
        int lenDiff = cellCount - scoreIdealLength;
        if (lenDiff < 0)
            lenDiff = -lenDiff;
        return perpDeps * scorePerpDepBonus - lenDiff * scoreLengthPenalty;
    }

    /// <summary>
    /// Tries to spawn one pending arrow. On success, <paramref name="weight"/>
    /// is the arrow's garbage-point cost: <c>weightPerArrow + perpDeps *
    /// weightPerPerpDep</c>. Length plays no role — long collinear arrows are
    /// trivial chains; what makes an arrow hard is how much cross-traffic it
    /// already passes through. On failure, returns false and weight is 0.
    /// </summary>
    private bool TrySpawnOnePending(float commitAt, out int weight)
    {
        weight = 0;
        var pending = _session.TrySpawnPending(commitAt, spawnCandidates, ScoreCandidate);
        if (pending == null)
            return false;

        weight = weightPerArrow + pending.PerpForwardDeps * weightPerPerpDep;
        if (weight < 1)
            weight = 1;
        _boardView.AddArrowView(pending.Arrow);
        ArrowView view = _boardView.GetArrowView(pending.Arrow);
        if (view != null)
        {
            view.EnterPendingMode();
            // Stamp this arrow with the active combo's palette color. The
            // color sticks through the pending → committed transition because
            // CommitPending no longer recolors via ApplyColoring.
            _boardView.SetArrowColor(pending.Arrow, ColorForCombo(_activeCombo));
        }
        return true;
    }

    private int ComputeComboSize()
    {
        // Combo size = fixed baseline + occupancy catch-up bonus (positive
        // only, ramps from +scale at empty to 0 at target via a quarter-
        // cosine, clamped at 0 above). Difficulty progression doesn't
        // touch combo size — it accelerates push cadence instead.
        float occupancy = OccupancyRatio();
        float adaptive = 0f;
        if (occupancy < targetOccupancy && targetOccupancy > 0f)
        {
            // t in [0, 1] across [0, target]; cos(t * π/2) gives 1 at empty,
            // 0 at target — smooth roll-off with no abrupt knee.
            float t = occupancy / targetOccupancy;
            adaptive = Mathf.Cos(t * Mathf.PI * 0.5f) * occupancyAdaptiveScale;
        }
        int size = comboBaseRate + Mathf.RoundToInt(adaptive);
        if (size < 1)
            size = 1;
        return size;
    }

    /// <summary>
    /// Power-law difficulty tier.
    /// <c>tier = floor((clears / firstTier)^power)</c>. With default
    /// <c>firstTier=10, power=0.78</c>: tiers fire near 10, 25, 43, 64, 87,
    /// 113, 140, 168, 198, 230, 263, 297, 332, 367 clears. Used to ramp
    /// the push interval downward over time.
    /// </summary>
    private int ComputeDifficultyTier()
    {
        if (ClearCount < clearsForFirstTier)
            return 0;
        float ratio = ClearCount / (float)clearsForFirstTier;
        return Mathf.FloorToInt(Mathf.Pow(ratio, difficultyTierPower));
    }

    /// <summary>
    /// Current push interval given progression. Linear step-down per
    /// difficulty tier, clamped at <see cref="pushIntervalMinSeconds"/>.
    /// </summary>
    private float CurrentPushIntervalSeconds()
    {
        float interval =
            pushIntervalAtStartSeconds - ComputeDifficultyTier() * pushIntervalStepDownPerTier;
        return interval < pushIntervalMinSeconds ? pushIntervalMinSeconds : interval;
    }

    /// <summary>
    /// Live occupancy ratio: real arrows + pending ghost arrows. Ghosts
    /// count because they're already taking up cells the player must keep
    /// clearable. Without this, the first push tick after an empty start
    /// would still see "occupancy = 0" and re-fire the full catch-up bonus
    /// even though the board already has ~9 points of pending arrows.
    /// </summary>
    private float OccupancyRatio()
    {
        Board board = _session.Board;
        int total = board.Width * board.Height;
        if (total <= 0)
            return 0f;
        int occupied = board.OccupiedCellCount;
        foreach (var p in _session.PendingArrows)
            occupied += p.Arrow.Cells.Count;
        return (float)occupied / total;
    }

    // ---- Update loop -------------------------------------------------------

    private readonly List<PendingArrow> _commitScratch = new();

    private void Update()
    {
        if (!_active)
            return;

        float now = Time.time;

        // Clear-streak combo timer (display-only).
        if (_currentCombo > 0 && _lastClearTime > 0f && now - _lastClearTime > comboTimerSeconds)
            BreakCombo();

        // Push tick (drives the garbage meter forward). Interval shrinks
        // with progression (see CurrentPushIntervalSeconds) — difficulty
        // escalates via cadence, not combo size or arrow quality.
        _pushTimer += Time.deltaTime;
        float interval = CurrentPushIntervalSeconds();
        if (_pushTimer >= interval)
        {
            _pushTimer -= interval;
            OnPushTick();
        }

        // Danger tint: alpha tracks shortfall presence + pulses.
        UpdateDangerTint();

        // Cleared-count display.
        if (_clearsLabel != null)
            _clearsLabel.text = ClearCount.ToString();

        // Commit pipeline: any pending whose commit deadline passed becomes real.
        _commitScratch.Clear();
        foreach (var pending in _session.PendingArrows)
            if (now >= pending.CommitAt)
                _commitScratch.Add(pending);

        foreach (var pending in _commitScratch)
            CommitPending(pending);
    }

    private void CommitPending(PendingArrow pending)
    {
        ArrowView view = _boardView.GetArrowView(pending.Arrow);
        if (view != null)
            view.ExitPendingMode();

        // Don't recolor via ApplyColoring here — the dependency-graph
        // palette assignment would overwrite the per-combo color we
        // stamped during TrySpawnOnePending. Endless deliberately groups
        // arrows by combo color for visual continuity from queue → board.
        _session.CommitPending(pending);
    }

    // ---- Danger tint -------------------------------------------------------

    private void UpdateDangerTint()
    {
        if (_camera == null)
            return;

        // Run is over — restore the original background color, regardless of
        // ordering quirks that may have called us after EndRun.
        if (!_active)
        {
            _camera.backgroundColor = _cameraBaseColor;
            return;
        }

        // Tint intensity is binary on shortfall presence. Pulses to make it
        // unmissable while uncommitted arrows are queued.
        float t = _activeShortfall > 0 ? 1f : 0f;
        float pulse = Mathf.Sin(Time.time * 2f * Mathf.PI * DangerPulseHz);
        float strength = (t * DangerMaxAlpha) + (t * DangerPulseAmp * pulse);
        if (strength < 0f)
            strength = 0f;
        if (strength > 1f)
            strength = 1f;

        // Lerp the camera clear color from base toward red. Arrows render on
        // top of this clear so they remain fully visible — only the empty
        // space behind/around them takes on the danger hue.
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
        if (_meterContainer == null)
            return;

        _meterContainer.Clear();

        // Container is bottom-justified flex (column + flex-end). Newest
        // combo sits at the top of the stack and combos shift down each
        // push tick; the oldest pops from the bottom. Each combo renders
        // as one continuous bar in its assigned palette color, matching
        // the color of the arrows it spawns on commit.
        for (int i = _meter.Count - 1; i >= 0; i--)
        {
            var bar = new VisualElement();
            bar.AddToClassList("endless-meter-bar");
            bar.style.backgroundColor = ColorForCombo(_meter[i]);
            bar.style.height = _meter[i].Size * MeterPointPixelHeight;
            _meterContainer.Add(bar);
        }

        // Shortfall sits below the queue (last child = bottom). Always red
        // regardless of the active combo's color — this is the danger
        // signal, not a queue entry.
        if (_activeShortfall > 0)
        {
            var bar = new VisualElement();
            bar.AddToClassList("endless-meter-bar");
            bar.AddToClassList("endless-meter-bar--shortfall");
            bar.style.backgroundColor = MeterShortfallColor;
            bar.style.height = _activeShortfall * MeterPointPixelHeight;
            _meterContainer.Add(bar);
        }
    }

    // ---- End of run --------------------------------------------------------

    private void EndRun()
    {
        if (!_active)
            return;
        _active = false;
        _runEndTime = Time.time;
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

    public float RunDurationSeconds => (_active ? Time.time : _runEndTime) - _runStartTime;
}
