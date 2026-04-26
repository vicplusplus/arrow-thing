using System;
using System.Collections.Generic;

/// <summary>
/// Owns the live state for a singleplayer endless run: a finalized <see cref="Board"/>
/// holding real arrows, a long-lived <see cref="NativeGenerationState"/> serving
/// as the spawn-time cycle-detection cache, and a list of pending ghost arrows
/// awaiting commit.
///
/// Lifecycle:
///   1. Construct with an initial-fill Board (already finalized via
///      <see cref="BoardGeneration.FillBoardIncremental"/> with centermost-first).
///      The session rebuilds native state from the Board's arrows, reassigning
///      generation indices 0..N-1 densely (compaction may have left holes).
///   2. <see cref="TrySpawnPending"/> — generate a new ghost, place it in native
///      state for subsequent cycle checks, add to pending list. Returns null on
///      topout (generator exhaustion).
///   3. <see cref="CommitPending"/> — move a ghost to the real Board via
///      <see cref="Board.AddArrow"/>. Native state already has it placed;
///      commit only flips HashSet dep relationships live.
///   4. <see cref="ClearRealArrow"/> — clear a real arrow via
///      <see cref="Board.RemoveArrow"/> AND remove from native state (so
///      subsequent ghost spawns don't see it as a blocker).
///
/// Ghosts themselves are never "cleared"; they either commit or are dropped by
/// an external mechanic (not implemented in this prototype).
/// </summary>
public sealed class EndlessBoardSession
{
    public Board Board { get; }
    private NativeGenerationState _state;
    private readonly List<PendingArrow> _pending = new();
    private PortableRandom _rng;
    private readonly int _maxLength;

    public IReadOnlyList<PendingArrow> PendingArrows => _pending;

    /// <summary>
    /// Most recent spawn attempt hit topout (no candidate could be placed).
    /// Reset to false on the next successful spawn.
    /// </summary>
    public bool LastSpawnToppedOut { get; private set; }

    /// <summary>
    /// Constructs a session from a finalized Board. <paramref name="maxLength"/>
    /// is the arrow length cap for new ghost spawns (matches the initial-fill
    /// parameter). <paramref name="spawnSeed"/> seeds the ghost-generation RNG.
    /// </summary>
    public EndlessBoardSession(Board board, int maxLength, int spawnSeed)
    {
        Board = board ?? throw new ArgumentNullException(nameof(board));
        _maxLength = maxLength;
        _state = BuildStateFromBoard(board);

        var seedRng = new PortableRandom((uint)spawnSeed);
        _rng = new PortableRandom((uint)seedRng.NextInt(1, int.MaxValue));
    }

    /// <summary>
    /// Rebuilds a fresh NativeGenerationState from a finalized Board. Gen indices
    /// are reassigned 0..N-1 in Board.Arrows order (compaction may have left the
    /// original indices non-contiguous; this normalizes them for the run).
    /// </summary>
    private static NativeGenerationState BuildStateFromBoard(Board board)
    {
        var state = new NativeGenerationState(board.Width, board.Height);
        state.InitializeCandidates();

        for (int i = 0; i < board.Arrows.Count; i++)
        {
            Arrow arrow = board.Arrows[i];
            arrow._generationIndex = i;
            NativeGeneration.PlaceArrowExplicit(ref state, arrow.Cells, (int)arrow.HeadDirection);
        }

        return state;
    }

    /// <summary>
    /// Attempts to generate a new ghost arrow. On success, the arrow is placed
    /// into native state (for future cycle checks) and added to the pending
    /// list, then returned. On topout (no candidate placeable), returns null
    /// and sets <see cref="LastSpawnToppedOut"/> to true.
    /// </summary>
    public PendingArrow TrySpawnPending(float commitAt) =>
        TrySpawnPending(commitAt, numCandidates: 1, scoreFn: null);

    /// <summary>
    /// Multi-candidate variant: generates up to <paramref name="numCandidates"/>
    /// independent random placements, scores each via
    /// <paramref name="scoreFn"/>, and keeps the highest-scoring one.
    /// Returns null on topout. The score function receives
    /// <c>(cellCount, perpForwardDeps)</c> and returns a higher-is-better
    /// score. If <paramref name="scoreFn"/> is null, the first valid candidate
    /// is taken (legacy single-shot behavior).
    ///
    /// All candidates are validated against the full board solvability check
    /// (Kahn topo) so even if the incremental cycle detector accepts an
    /// invalid placement, the post-validate catches it. Centermost-first is
    /// OFF — uniform random head selection across the whole board, so
    /// generation isn't biased by ring distance (a fully-clear strip through
    /// center looks identical to deep-board for the centermost heuristic).
    /// </summary>
    public PendingArrow TrySpawnPending(
        float commitAt,
        int numCandidates,
        System.Func<int, int, int> scoreFn
    )
    {
        if (numCandidates < 1)
            numCandidates = 1;

        const int maxAttempts = 16; // safety cap on retries inside any one slot
        var winnerCells = (List<Cell>)null;
        int winnerDir = 0;
        int winnerScore = int.MinValue;

        for (int candidate = 0; candidate < numCandidates; candidate++)
        {
            List<Cell> cells = null;
            int genIndex = -1;
            int perpDeps = 0;
            int dir = 0;

            // Each candidate slot may need multiple raw attempts because the
            // generator can produce unsolvable placements that we then reject.
            bool found = false;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                bool placed = NativeGeneration.TryGenerateArrow(
                    ref _state,
                    _maxLength,
                    ref _rng,
                    centermostFirst: false
                );
                if (!placed)
                    break; // pool exhausted

                int cellCount = _state.lastArrowCellCount;
                var thisCells = new List<Cell>(cellCount);
                for (int i = 0; i < cellCount; i++)
                    thisCells.Add(new Cell(_state.scratchCellsX[i], _state.scratchCellsY[i]));
                int thisGenIndex = _state.lastPlacedIndex;
                int thisPerpDeps = _state.lastPlacedPerpDeps;
                int thisDir = _state.genDir[thisGenIndex];

                if (!NativeGeneration.IsBoardSolvable(ref _state))
                {
                    NativeGeneration.RemoveArrow(ref _state, thisGenIndex, thisCells);
                    continue;
                }

                cells = thisCells;
                genIndex = thisGenIndex;
                perpDeps = thisPerpDeps;
                dir = thisDir;
                found = true;
                break;
            }

            if (!found)
                break; // can't even produce one candidate, stop trying

            int score = scoreFn != null ? scoreFn(cells.Count, perpDeps) : 0;
            if (score > winnerScore)
            {
                winnerScore = score;
                winnerCells = cells;
                winnerDir = dir;
            }

            // Remove this candidate so the next attempt starts from the same
            // base state. (We re-place the winner explicitly after the loop.)
            NativeGeneration.RemoveArrow(ref _state, genIndex, cells);
        }

        if (winnerCells == null)
        {
            LastSpawnToppedOut = true;
            return null;
        }

        // Re-place the winner. PlaceArrowExplicit doesn't run cycle detection
        // (it's an explicit placement), but the candidate already passed
        // IsBoardSolvable when first placed and the board state is identical
        // now (we restored via RemoveArrow), so it's still valid.
        NativeGeneration.PlaceArrowExplicit(ref _state, winnerCells, winnerDir);
        int finalIdx = _state.lastPlacedIndex;
        int finalPerpDeps = _state.lastPlacedPerpDeps;

        LastSpawnToppedOut = false;
        var arrow = new Arrow(winnerCells);
        arrow._generationIndex = finalIdx;
        var pending = new PendingArrow(arrow, finalIdx, commitAt, finalPerpDeps);
        _pending.Add(pending);
        return pending;
    }

    /// <summary>
    /// Commits a ghost: adds the underlying arrow to the Board as real. Native
    /// state already has the arrow placed (from spawn time) — this call only
    /// updates Board's HashSet dep graph and occupancy. Throws if the ghost is
    /// not currently pending.
    /// </summary>
    public void CommitPending(PendingArrow pending)
    {
        if (pending == null)
            throw new ArgumentNullException(nameof(pending));
        if (!_pending.Remove(pending))
            throw new InvalidOperationException("PendingArrow is not in this session.");

        // Board.AddArrow updates occupancy and HashSet deps. Board's own bitset
        // arrays are null after FinalizeGenerationIncremental, so AddArrow's
        // generation-index branch is skipped — the arrow's existing
        // _generationIndex (assigned at spawn) is preserved.
        Board.AddArrow(pending.Arrow);
    }

    /// <summary>
    /// Drops a ghost without committing. Removes from the pending list AND from
    /// native state (so its cells become available again for future ghost spawns
    /// and so it no longer contributes to cycle checks). Intended for use by
    /// garbage-cancel mechanics in the PvP milestone; exposed here for
    /// completeness and testing.
    /// </summary>
    public void DropPending(PendingArrow pending)
    {
        if (pending == null)
            throw new ArgumentNullException(nameof(pending));
        if (!_pending.Remove(pending))
            throw new InvalidOperationException("PendingArrow is not in this session.");

        NativeGeneration.RemoveArrow(ref _state, pending.GenIndex, pending.Arrow.Cells);
    }

    /// <summary>
    /// Clears a real arrow: removes from the Board (HashSet dep graph) AND from
    /// native state (bitset dep graph + ray index + occupancy). The order is
    /// safe — Board.RemoveArrow validates clearability, native RemoveArrow is
    /// unconditional.
    /// </summary>
    public void ClearRealArrow(Arrow arrow)
    {
        if (arrow == null)
            throw new ArgumentNullException(nameof(arrow));
        Board.RemoveArrow(arrow);
        NativeGeneration.RemoveArrow(ref _state, arrow._generationIndex, arrow.Cells);
    }

    /// <summary>
    /// Twice-Chebyshev distance of the centermost available head candidate in
    /// the spawn pool. Used by the danger-tint UI: when this exceeds the
    /// outer-wiggle threshold, the board is close to topout. Returns -1 if no
    /// candidates remain (i.e. already topped out).
    /// </summary>
    public int CentermostCandidateRing()
    {
        int c2x = _state.boardWidth - 1;
        int c2y = _state.boardHeight - 1;
        int w = _state.boardWidth;

        int minDist = int.MaxValue;
        bool anyLive = false;

        for (int i = 0; i < _state.candidateCount; i++)
        {
            NativeArrowHeadData c = _state.candidates[i];
            // Skip stale candidates (cells occupied) — mirror of the lazy
            // pruning that TryGenerateArrow does on rejection. This is a
            // read-only preview of ring pressure, so don't mutate the pool.
            if (
                _state.occupancy[c.headY * w + c.headX] >= 0
                || _state.occupancy[c.nextY * w + c.nextX] >= 0
            )
                continue;

            int dx = (c.headX << 1) - c2x;
            int dy = (c.headY << 1) - c2y;
            if (dx < 0)
                dx = -dx;
            if (dy < 0)
                dy = -dy;
            int d = dx > dy ? dx : dy;
            if (d < minDist)
            {
                minDist = d;
                anyLive = true;
            }
        }

        return anyLive ? minDist : -1;
    }
}
