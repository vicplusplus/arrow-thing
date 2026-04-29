using System;
using System.Collections.Generic;

/// <summary>
/// One pending combo waiting in the meter. Sized in garbage points (roughly
/// arrow cell count). Color index points into the active palette and is
/// reproduced deterministically on replay (see <see cref="EndlessRun"/>'s
/// color RNG).
/// </summary>
public readonly struct EndlessMeterEntry
{
    public readonly int Size;
    public readonly int ColorIndex;

    public EndlessMeterEntry(int size, int colorIndex)
    {
        Size = size;
        ColorIndex = colorIndex;
    }
}

/// <summary>
/// Pure-C# endless-mode game loop. Owns the entire run lifecycle:
/// sim-time clock, push schedule, garbage meter, commit pipeline, clear
/// handling, score accumulation. Unity-agnostic so the same code drives
/// both the live game (<see cref="EndlessModeController"/>) and the
/// server-side replay verifier.
///
/// <para><b>Three driving APIs</b>:</para>
/// <list type="bullet">
///   <item><see cref="Advance"/> — caller pushes deltaTime; the run fires
///   any push ticks + commits whose sim-time threshold has been crossed.</item>
///   <item><see cref="HandleTap"/> — caller passes a cell coordinate; the
///   run resolves it against current board state and returns the kind
///   (Missed / Blocked / Cleared). Verifier compares this to the kind
///   stored on the replay event.</item>
///   <item>Events (<see cref="PendingSpawned"/>, <see cref="PendingCommitted"/>,
///   <see cref="MeterChanged"/>, <see cref="ShortfallChanged"/>,
///   <see cref="ToppedOut"/>) — the view subscribes for HUD/render reactions;
///   the verifier ignores them.</item>
/// </list>
///
/// <para>Determinism contract: identical {board, spawnSeed, paletteCount,
/// tuning} + identical sequence of {Advance(dt), HandleTap(cell)} calls
/// produces identical {ClearCount, LongestCombo, RunDurationSeconds} and
/// the same internal meter / shortfall trajectory. The push-tick scheduler
/// uses canonical sim-time thresholds (<see cref="_lastPushSimTime"/> + interval),
/// not delta-accumulation, so frame jitter on the client doesn't desync
/// the verifier.</para>
/// </summary>
public sealed class EndlessRun
{
    private readonly EndlessTuning _tuning;
    private readonly int _paletteCount;
    private readonly EndlessBoardSession _board;
    private readonly PortableRandom _colorRng;

    private float _simTime;
    private float _lastPushSimTime;
    private float _lastClearTime = -1f;
    private int _activeShortfall;
    private EndlessMeterEntry _activeCombo;
    private readonly List<EndlessMeterEntry> _meter = new();
    private int _lastColorIndex = -1;
    private int _currentCombo;
    private float _runEndTime;
    private bool _active = true;

    private readonly List<PendingArrow> _commitScratch = new();

    // ---- Read-only state (consumed by view + verifier) --------------------

    public int ClearCount { get; private set; }
    public int LongestCombo { get; private set; }
    public int CurrentCombo => _currentCombo;
    public float SimTime => _simTime;

    /// <summary>
    /// Sim-time elapsed since run start. Locks at run end so the result
    /// screen + replay payload + verifier all see the same value.
    /// </summary>
    public float RunDurationSeconds => _active ? _simTime : _runEndTime;

    public bool IsActive => _active;

    /// <summary>The shared <see cref="Board"/> the run mutates as combos commit and player taps clear arrows.</summary>
    public Board Board => _board.Board;

    public IReadOnlyList<PendingArrow> PendingArrows => _board.PendingArrows;

    public IReadOnlyList<EndlessMeterEntry> Meter => _meter;

    public int ActiveShortfall => _activeShortfall;

    public EndlessMeterEntry ActiveCombo => _activeCombo;

    // ---- Events (view-only; verifier ignores) -----------------------------

    /// <summary>Fired when a pending arrow is spawned. Argument 2 = palette color index.</summary>
    public event Action<PendingArrow, int> PendingSpawned;

    /// <summary>Fired when a pending arrow's preview deadline passes and it commits to a real arrow.</summary>
    public event Action<PendingArrow> PendingCommitted;

    /// <summary>Fired whenever the meter contents change (combo pushed, popped, or shortfall updated). View rebuilds its UI.</summary>
    public event Action MeterChanged;

    /// <summary>Fired when the active shortfall transitions to / from zero. View toggles the danger tint.</summary>
    public event Action ShortfallChanged;

    /// <summary>Fired the moment topout is detected. Argument = sim time of the topout.</summary>
    public event Action<float> ToppedOut;

    // ---- Construction -----------------------------------------------------

    public EndlessRun(Board board, int spawnSeed, int paletteCount, EndlessTuning tuning)
    {
        if (board == null)
            throw new ArgumentNullException(nameof(board));
        _tuning = tuning;
        _paletteCount = paletteCount;
        _board = new EndlessBoardSession(board, tuning.EndlessMaxArrowLength, spawnSeed);

        // Color-pick RNG, seeded independently from the spawn RNG so adding
        // / removing color picks doesn't perturb spawn outcomes.
        var colorSeedRng = new PortableRandom((uint)spawnSeed ^ 0x9E3779B9u);
        _colorRng = new PortableRandom((uint)colorSeedRng.NextInt(1, int.MaxValue));

        // Seed the meter with one combo so the first push tick has something
        // to send to pending instead of an empty queue.
        _meter.Add(NewComboEntry());

        // Fire the first push immediately so an empty start board doesn't
        // leave the player staring at blank space until the first interval.
        // Subscribers attached after construction won't see these events;
        // they should iterate PendingArrows post-construction with the
        // active combo's color and synthesise their own catch-up handling.
        OnPushTick();
    }

    // ---- Driving APIs -----------------------------------------------------

    /// <summary>
    /// Advances sim time by <paramref name="deltaTime"/>. Fires any push
    /// ticks + pending commits whose sim-time thresholds were crossed.
    /// Caller drives this from Update().Time.deltaTime (live game) or
    /// from event-to-event sim-time deltas (verifier).
    /// </summary>
    public void Advance(float deltaTime)
    {
        if (!_active || deltaTime <= 0f)
            return;

        _simTime += deltaTime;

        // Combo timer (display-only). Resets the combo streak if too long
        // between clears.
        if (
            _currentCombo > 0
            && _lastClearTime > 0f
            && _simTime - _lastClearTime > _tuning.ComboTimerSeconds
        )
            _currentCombo = 0;

        // Canonical push scheduler: fire every push whose threshold has
        // been crossed. While-loop catches up if a long delta straddles
        // multiple intervals (verifier may pass big deltas between events).
        while (_active && _simTime >= _lastPushSimTime + CurrentPushIntervalSeconds())
        {
            _lastPushSimTime += CurrentPushIntervalSeconds();
            OnPushTick();
        }

        // Commit any pending whose preview deadline passed.
        ProcessCommits();
    }

    /// <summary>
    /// Resolves a player tap at the given grid position against the current
    /// board. Returns the result that actually applies — does NOT trust
    /// caller-stated outcome. Server-side verifier compares the returned
    /// result to the result stored on the replay event; mismatch flags the
    /// user.
    /// </summary>
    /// <param name="gridX">Float grid X (1 unit per cell). Run rounds to the nearest cell internally.</param>
    /// <param name="gridY">Float grid Y (1 unit per cell). Run rounds to the nearest cell internally.</param>
    public TapResult HandleTap(float gridX, float gridY)
    {
        if (!_active)
            return TapResult.Missed;

        var cell = new Cell(
            (int)Math.Round(gridX, MidpointRounding.AwayFromZero),
            (int)Math.Round(gridY, MidpointRounding.AwayFromZero)
        );
        if (!Board.Contains(cell))
            return TapResult.Missed;

        var arrow = Board.GetArrowAt(cell);
        if (arrow == null)
            return TapResult.Missed;

        if (!Board.IsClearable(arrow))
            return TapResult.Blocked;

        // Cleared — apply state changes.
        _board.ClearRealArrow(arrow);
        ClearCount++;
        _currentCombo++;
        if (_currentCombo > LongestCombo)
            LongestCombo = _currentCombo;
        _lastClearTime = _simTime;

        // Immediate-mode shortfall placement: a clear may have freed room
        // for the previous combo's leftover garbage. Try to chip away at
        // the shortfall right now, before the next push tick which would
        // otherwise topout if shortfall > 0 at that point.
        if (_activeShortfall > 0)
        {
            float commitAt = _simTime + CurrentPreviewDurationSeconds();
            int prevShortfall = _activeShortfall;
            while (_activeShortfall > 0)
            {
                if (!TrySpawnOnePending(commitAt, out int weight))
                    break;
                _activeShortfall -= weight;
            }
            if (_activeShortfall < 0)
                _activeShortfall = 0;
            if (_activeShortfall != prevShortfall)
            {
                MeterChanged?.Invoke();
                ShortfallChanged?.Invoke();
            }
        }

        return TapResult.Cleared;
    }

    // ---- Internal: scheduler ----------------------------------------------

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
        // whatever doesn't fit becomes shortfall.
        bool shortfallChanged = false;
        if (_meter.Count > 0)
        {
            _activeCombo = _meter[0];
            _meter.RemoveAt(0);
            int placed = SpawnComboPoints(_activeCombo.Size);
            int shortfall = _activeCombo.Size - placed;
            int newShortfall = shortfall < 0 ? 0 : shortfall;
            if (newShortfall != _activeShortfall)
                shortfallChanged = true;
            _activeShortfall = newShortfall;
        }

        // Push a new combo to the back so there's always something queued.
        _meter.Add(NewComboEntry());
        MeterChanged?.Invoke();
        if (shortfallChanged)
            ShortfallChanged?.Invoke();
    }

    private void ProcessCommits()
    {
        _commitScratch.Clear();
        foreach (var pending in _board.PendingArrows)
            if (_simTime >= pending.CommitAt)
                _commitScratch.Add(pending);

        foreach (var pending in _commitScratch)
        {
            _board.CommitPending(pending);
            PendingCommitted?.Invoke(pending);
        }
    }

    private int SpawnComboPoints(int comboPoints)
    {
        float commitAt = _simTime + CurrentPreviewDurationSeconds();
        int placed = 0;
        while (placed < comboPoints)
        {
            if (!TrySpawnOnePending(commitAt, out int weight))
                break;
            placed += weight;
        }
        return placed;
    }

    private bool TrySpawnOnePending(float commitAt, out int weight)
    {
        weight = 0;
        var pending = _board.TrySpawnPending(commitAt, _tuning.SpawnCandidates, ScoreCandidate);
        if (pending == null)
            return false;

        weight = _tuning.WeightPerArrow + pending.PerpForwardDeps * _tuning.WeightPerPerpDep;
        if (weight < 1)
            weight = 1;

        PendingSpawned?.Invoke(pending, _activeCombo.ColorIndex);
        return true;
    }

    private int ScoreCandidate(int cellCount, int perpDeps)
    {
        int lenDiff = cellCount - _tuning.ScoreIdealLength;
        if (lenDiff < 0)
            lenDiff = -lenDiff;
        return perpDeps * _tuning.ScorePerpDepBonus - lenDiff * _tuning.ScoreLengthPenalty;
    }

    private EndlessMeterEntry NewComboEntry()
    {
        int colorIdx = PickComboColorIndex();
        _lastColorIndex = colorIdx;
        return new EndlessMeterEntry(ComputeComboSize(), colorIdx);
    }

    private int PickComboColorIndex()
    {
        if (_paletteCount <= 0)
            return 0;
        if (_paletteCount == 1 || _lastColorIndex < 0)
            return _colorRng.NextInt(_paletteCount);
        int idx = _colorRng.NextInt(_paletteCount - 1);
        if (idx >= _lastColorIndex)
            idx++;
        return idx;
    }

    private int ComputeComboSize()
    {
        int boardCells = Board.Width * Board.Height;
        float occupancy = OccupancyRatio();
        float adaptive = 0f;
        if (occupancy < _tuning.TargetOccupancy && _tuning.TargetOccupancy > 0f)
        {
            float t = occupancy / _tuning.TargetOccupancy;
            // System.Math.Cos returns double; cast to float. Matches
            // UnityEngine.Mathf.Cos which is also float (and itself wraps Math.Cos).
            adaptive =
                (float)Math.Cos(t * Math.PI * 0.5)
                * _tuning.OccupancyAdaptivePctOfBoard
                * boardCells;
        }
        int size = (int)
            Math.Round(
                _tuning.ComboBasePctOfBoard * boardCells + adaptive,
                MidpointRounding.AwayFromZero
            );
        int maxSize = Math.Max(1, (int)Math.Ceiling(_tuning.MaxComboPctOfBoard * boardCells));
        if (size > maxSize)
            size = maxSize;
        if (size < 1)
            size = 1;
        return size;
    }

    private float OccupancyRatio()
    {
        int total = Board.Width * Board.Height;
        if (total <= 0)
            return 0f;
        int occupied = Board.OccupiedCellCount;
        foreach (var p in _board.PendingArrows)
            occupied += p.Arrow.Cells.Count;
        return (float)occupied / total;
    }

    private int ScaledClearsForFirstTier()
    {
        int boardCells = Board.Width * Board.Height;
        int scaled = (int)Math.Round(_tuning.ClearsForFirstTierAt100Cells * boardCells / 100.0);
        return Math.Max(1, scaled);
    }

    private int ComputeDifficultyTier()
    {
        int firstTier = ScaledClearsForFirstTier();
        if (ClearCount < firstTier)
            return 0;
        float ratio = ClearCount / (float)firstTier;
        return (int)Math.Floor(Math.Pow(ratio, _tuning.DifficultyTierPower));
    }

    private float CurrentPushIntervalSeconds()
    {
        float interval =
            _tuning.PushIntervalAtStartSeconds
            - ComputeDifficultyTier() * _tuning.PushIntervalStepDownPerTier;
        return interval < _tuning.PushIntervalMinSeconds
            ? _tuning.PushIntervalMinSeconds
            : interval;
    }

    private float CurrentPreviewDurationSeconds()
    {
        return CurrentPushIntervalSeconds() * _tuning.PreviewDurationFractionOfPush;
    }

    private void EndRun()
    {
        if (!_active)
            return;
        _active = false;
        _runEndTime = _simTime;
        ToppedOut?.Invoke(_simTime);
    }
}
