using System.Collections.Generic;

public sealed class Board
{
    internal readonly List<Arrow> _arrows = new();
    internal readonly HashSet<Arrow> _arrowSet = new();
    internal readonly Arrow[,] _occupancy;
    private readonly Dictionary<Arrow, HashSet<Arrow>> _dependsOn = new();
    private readonly Dictionary<Arrow, HashSet<Arrow>> _dependedOnBy = new();

    // Spatial ray index: arrows grouped by head direction and row/column.
    // For a cell (cx, cy), arrows whose ray crosses it are:
    //   Right-facing on row cy with head.X < cx, Left-facing on row cy with head.X > cx,
    //   Up-facing on col cx with head.Y < cy, Down-facing on col cx with head.Y > cy.
    private readonly List<Arrow>[] _rightHeadsByRow;
    private readonly List<Arrow>[] _leftHeadsByRow;
    private readonly List<Arrow>[] _upHeadsByCol;
    private readonly List<Arrow>[] _downHeadsByCol;

    // Generation candidate pool (null until InitializeForGeneration)
    internal List<ArrowHeadData> _availableArrowHeads;

    // Bitset-based dependency storage for generation (null until InitializeForGeneration)
    internal int _bitsetWords;
    internal ulong[] _depsBitsFlat; // [arrowIndex * _bitsetWords + word]
    internal bool[] _hasAnyDeps; // per-arrow flag: true if dep bitset row is non-empty

    // Sparse nonzero word tracking: for each arrow, which words in its dep row are nonzero.
    // Allows BFS to skip zero words (huge win when activeWords >> actual dep count).
    internal const int MaxNonZeroTracked = 16;
    internal int[] _depsNonZeroWords; // [idx * MaxNonZeroTracked + i] = word index
    internal int[] _depsNonZeroCount; // [idx] = count of tracked nonzero words (-1 = overflow)
    internal int _nextGenIndex;

    // Arrow head/direction flat arrays for fast index→geometry lookup during early-abort cycle detection
    internal int[] _genHeadX;
    internal int[] _genHeadY;
    internal Arrow.Direction[] _genDir;

    public IReadOnlyList<Arrow> Arrows => _arrows;
    public int Width { get; }
    public int Height { get; }
    public int OccupiedCellCount { get; internal set; }

    /// <summary>Number of candidates at the time InitializeForGeneration was called.</summary>
    public int InitialCandidateCount { get; internal set; }

    /// <summary>Number of remaining unpruned head candidates. 0 before initialization.
    /// During Burst generation, updated by <see cref="BoardGeneration.FillBoardIncremental"/>.</summary>
    public int RemainingCandidateCount =>
        _availableArrowHeads != null ? _availableArrowHeads.Count : _nativeRemainingCandidates;
    internal int _nativeRemainingCandidates;

    public Board(int width, int height)
    {
        Width = width;
        Height = height;
        _occupancy = new Arrow[width, height];

        _rightHeadsByRow = new List<Arrow>[height];
        _leftHeadsByRow = new List<Arrow>[height];
        for (int y = 0; y < height; y++)
        {
            _rightHeadsByRow[y] = new List<Arrow>();
            _leftHeadsByRow[y] = new List<Arrow>();
        }

        _upHeadsByCol = new List<Arrow>[width];
        _downHeadsByCol = new List<Arrow>[width];
        for (int x = 0; x < width; x++)
        {
            _upHeadsByCol[x] = new List<Arrow>();
            _downHeadsByCol[x] = new List<Arrow>();
        }
    }

    public void InitializeForGeneration()
    {
        _availableArrowHeads = CreateInitialArrowHeads();
        InitialCandidateCount = _availableArrowHeads.Count;

        int maxArrows = Width * Height / 2;
        // Use a compact stride: typically only ~25% of max arrows are placed.
        // Start at maxArrows/3 and grow if exceeded.
        int estimatedCapacity = System.Math.Max(64, maxArrows / 4);
        _bitsetWords = (estimatedCapacity + 63) >> 6;
        _depsBitsFlat = new ulong[maxArrows * _bitsetWords];
        _hasAnyDeps = new bool[maxArrows];
        _depsNonZeroWords = new int[maxArrows * MaxNonZeroTracked];
        _depsNonZeroCount = new int[maxArrows];
        _genHeadX = new int[maxArrows];
        _genHeadY = new int[maxArrows];
        _genDir = new Arrow.Direction[maxArrows];
        _nextGenIndex = 0;
    }

    /// <summary>Doubles bitset stride capacity, reallocating the flat array.</summary>
    internal void GrowBitsetCapacity()
    {
        int oldWords = _bitsetWords;
        int maxArrows = Width * Height / 2;
        _bitsetWords = System.Math.Min(oldWords * 2, (maxArrows + 63) >> 6);
        ulong[] newFlat = new ulong[maxArrows * _bitsetWords];
        for (int i = 0; i < _nextGenIndex; i++)
            System.Array.Copy(_depsBitsFlat, i * oldWords, newFlat, i * _bitsetWords, oldWords);
        _depsBitsFlat = newFlat;
    }

    /// <summary>
    /// Copies generation state from native arrays back into Board's managed arrays.
    /// Called after Burst generation completes to prepare for managed compaction.
    /// Assumes <see cref="_arrows"/> and <see cref="_arrowSet"/> are already populated.
    /// </summary>
    internal void InitializeFromNativeGeneration(ref NativeGenerationState state)
    {
        int maxArrows = Width * Height / 2;
        _bitsetWords = state.bitsetWords;
        _nextGenIndex = state.nextGenIndex;

        // Allocate and copy bitset arrays
        _depsBitsFlat = new ulong[maxArrows * _bitsetWords];
        int bitsetLen = _nextGenIndex * _bitsetWords;
        for (int i = 0; i < bitsetLen; i++)
            _depsBitsFlat[i] = state.depsBitsFlat[i];

        _hasAnyDeps = new bool[maxArrows];
        for (int i = 0; i < _nextGenIndex; i++)
            _hasAnyDeps[i] = state.hasAnyDeps[i];

        _depsNonZeroWords = new int[maxArrows * MaxNonZeroTracked];
        for (int i = 0; i < _nextGenIndex * MaxNonZeroTracked; i++)
            _depsNonZeroWords[i] = state.depsNonZeroWords[i];

        _depsNonZeroCount = new int[maxArrows];
        for (int i = 0; i < _nextGenIndex; i++)
            _depsNonZeroCount[i] = state.depsNonZeroCount[i];

        _genHeadX = new int[maxArrows];
        _genHeadY = new int[maxArrows];
        _genDir = new Arrow.Direction[maxArrows];
        for (int i = 0; i < _nextGenIndex; i++)
        {
            _genHeadX[i] = state.genHeadX[i];
            _genHeadY[i] = state.genHeadY[i];
            _genDir[i] = (Arrow.Direction)state.genDir[i];
        }

        // Build occupancy from arrows
        foreach (Arrow a in _arrows)
        foreach (Cell c in a.Cells)
            _occupancy[c.X, c.Y] = a;

        // Build ray index from arrows
        foreach (Arrow a in _arrows)
            AddToRayIndex(a);
    }

    /// <summary>
    /// Incrementally restores arrows from a snapshot. Places each arrow into occupancy
    /// and yields the count placed so far (for progress reporting). After all arrows are
    /// placed, builds the dependency graph in one forward-ray pass. Much faster than
    /// calling AddArrow individually because it avoids the O(n²) reverse-dependency scan.
    /// Caller must exhaust the enumerator for the board to be usable.
    /// </summary>
    public IEnumerator<int> RestoreArrowsIncremental(IReadOnlyList<Arrow> arrows)
    {
        // Phase 1: place arrows into occupancy, yielding after each
        foreach (Arrow arrow in arrows)
        {
            _arrows.Add(arrow);
            _arrowSet.Add(arrow);
            foreach (Cell c in arrow.Cells)
                _occupancy[c.X, c.Y] = arrow;
            OccupiedCellCount += arrow.Cells.Count;
            _dependsOn[arrow] = new HashSet<Arrow>();
            _dependedOnBy[arrow] = new HashSet<Arrow>();
            AddToRayIndex(arrow);
            yield return _arrows.Count;
        }

        // Phase 2: build dependency graph — each arrow's forward ray hits are its deps
        // Yields after each arrow so the caller's time budget can break between iterations.
        int depsBuilt = 0;
        foreach (Arrow arrow in arrows)
        {
            (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
            Cell cursor = new(arrow.HeadCell.X + dx, arrow.HeadCell.Y + dy);
            while (Contains(cursor))
            {
                Arrow hit = _occupancy[cursor.X, cursor.Y];
                if (hit != null && hit != arrow)
                {
                    _dependsOn[arrow].Add(hit);
                    _dependedOnBy[hit].Add(arrow);
                }
                cursor = new(cursor.X + dx, cursor.Y + dy);
            }
            depsBuilt++;
            yield return arrows.Count + depsBuilt;
        }
    }

    public void AddArrow(Arrow arrow)
    {
        // Validations
        if (arrow == null)
            throw new System.ArgumentNullException(nameof(arrow));
        if (_arrowSet.Contains(arrow))
            throw new System.InvalidOperationException("Arrow is already on the board.");
        foreach (Cell c in arrow.Cells)
        {
            if (!Contains(c))
                throw new System.ArgumentException(
                    $"Cell ({c.X}, {c.Y}) is out of bounds for board {Width}x{Height}."
                );
            if (_occupancy[c.X, c.Y] != null)
                throw new System.InvalidOperationException(
                    $"Cell ({c.X}, {c.Y}) is already occupied."
                );
        }

        // Assign generation index if generation is active
        if (_depsBitsFlat != null && arrow._generationIndex < 0)
        {
            arrow._generationIndex = _nextGenIndex++;
            int gIdx = arrow._generationIndex;
            _genHeadX[gIdx] = arrow.HeadCell.X;
            _genHeadY[gIdx] = arrow.HeadCell.Y;
            _genDir[gIdx] = arrow.HeadDirection;
        }

        // Set occupancy
        _arrows.Add(arrow);
        _arrowSet.Add(arrow);
        foreach (Cell c in arrow.Cells)
            _occupancy[c.X, c.Y] = arrow;
        OccupiedCellCount += arrow.Cells.Count;

        // Forward deps: this arrow depends on all existing arrows in its ray
        var deps = new HashSet<Arrow>();
        (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
        Cell cursor = new(arrow.HeadCell.X + dx, arrow.HeadCell.Y + dy);
        while (Contains(cursor))
        {
            Arrow hit = _occupancy[cursor.X, cursor.Y];
            if (hit != null && hit != arrow)
                deps.Add(hit);
            cursor = new(cursor.X + dx, cursor.Y + dy);
        }
        _dependsOn[arrow] = deps;
        foreach (Arrow dep in deps)
            _dependedOnBy[dep].Add(arrow);

        // Update bitset deps for forward deps
        if (_depsBitsFlat != null)
        {
            int nIdx = arrow._generationIndex;
            int offset = nIdx * _bitsetWords;
            foreach (Arrow dep in deps)
            {
                int dIdx = dep._generationIndex;
                if (dIdx >= 0)
                    _depsBitsFlat[offset + (dIdx >> 6)] |= 1UL << (dIdx & 63);
            }
        }

        // Reverse deps: existing arrows whose rays pass through this arrow's cells
        // Uses the spatial ray index for O(crossing) instead of O(N) scan.
        // Must run BEFORE AddToRayIndex so the new arrow doesn't match itself.
        var revDeps = new HashSet<Arrow>();
        foreach (Cell c in arrow.Cells)
        {
            int cx = c.X,
                cy = c.Y;
            foreach (Arrow a in _rightHeadsByRow[cy])
                if (a.HeadCell.X < cx && revDeps.Add(a))
                    _dependsOn[a].Add(arrow);
            foreach (Arrow a in _leftHeadsByRow[cy])
                if (a.HeadCell.X > cx && revDeps.Add(a))
                    _dependsOn[a].Add(arrow);
            foreach (Arrow a in _upHeadsByCol[cx])
                if (a.HeadCell.Y < cy && revDeps.Add(a))
                    _dependsOn[a].Add(arrow);
            foreach (Arrow a in _downHeadsByCol[cx])
                if (a.HeadCell.Y > cy && revDeps.Add(a))
                    _dependsOn[a].Add(arrow);
        }
        _dependedOnBy[arrow] = revDeps;

        // Update bitset deps for reverse deps
        if (_depsBitsFlat != null)
        {
            int nIdx = arrow._generationIndex;
            foreach (Arrow a in revDeps)
            {
                int aIdx = a._generationIndex;
                if (aIdx >= 0)
                    _depsBitsFlat[aIdx * _bitsetWords + (nIdx >> 6)] |= 1UL << (nIdx & 63);
            }
        }

        AddToRayIndex(arrow);

        // Candidate pruning is handled lazily in TryGenerateArrow via occupancy checks
    }

    /// <summary>
    /// Removes an arrow during generation: clears occupancy, ray index, and bitset deps.
    /// The generation index becomes dead (wasted slot).
    /// Internal: called by <see cref="BoardGeneration.CompactBoardInPlace"/> during post-process compaction.
    /// </summary>
    internal void RemoveArrowForGeneration(Arrow arrow)
    {
        int deadIdx = arrow._generationIndex;

        // Clear occupancy
        foreach (Cell c in arrow.Cells)
            _occupancy[c.X, c.Y] = null;
        OccupiedCellCount -= arrow.Cells.Count;

        // Remove from collections
        _arrows.Remove(arrow);
        _arrowSet.Remove(arrow);

        // Remove from ray index
        RemoveFromRayIndex(arrow);

        // Zero the dead arrow's dep row
        System.Array.Clear(_depsBitsFlat, deadIdx * _bitsetWords, _bitsetWords);
        _hasAnyDeps[deadIdx] = false;
        _depsNonZeroCount[deadIdx] = 0;

        // Clear dead arrow's bit from all other arrows' dep rows
        int word = deadIdx >> 6;
        ulong mask = ~(1UL << (deadIdx & 63));
        for (int i = 0; i < _nextGenIndex; i++)
            _depsBitsFlat[i * _bitsetWords + word] &= mask;

        // Mark as dead
        arrow._generationIndex = -1;
    }

    /// <summary>
    /// Fast path for generation: updates occupancy, ray index, and bitset deps only.
    /// Skips HashSet dependency tracking. Call <see cref="FinalizeGenerationIncremental"/> (or <see cref="FinalizeGeneration"/>) when done.
    /// </summary>
    internal void AddArrowForGeneration(Arrow arrow)
    {
        arrow._generationIndex = _nextGenIndex++;
        if (arrow._generationIndex >= _bitsetWords * 64)
            GrowBitsetCapacity();

        int nIdx = arrow._generationIndex;
        _genHeadX[nIdx] = arrow.HeadCell.X;
        _genHeadY[nIdx] = arrow.HeadCell.Y;
        _genDir[nIdx] = arrow.HeadDirection;

        _arrows.Add(arrow);
        _arrowSet.Add(arrow);
        foreach (Cell c in arrow.Cells)
            _occupancy[c.X, c.Y] = arrow;
        OccupiedCellCount += arrow.Cells.Count;

        // Forward deps: walk ray, set bits
        bool hasDeps = false;
        (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
        int cx = arrow.HeadCell.X + dx,
            cy = arrow.HeadCell.Y + dy;
        while (cx >= 0 && cx < Width && cy >= 0 && cy < Height)
        {
            Arrow hit = _occupancy[cx, cy];
            if (hit != null)
            {
                int dIdx = hit._generationIndex;
                if (dIdx >= 0)
                {
                    int word = dIdx >> 6;
                    SetDepBit(nIdx, word, 1UL << (dIdx & 63));
                    hasDeps = true;
                }
            }
            cx += dx;
            cy += dy;
        }
        _hasAnyDeps[nIdx] = hasDeps;

        // Reverse deps: existing arrows whose rays cross this arrow's cells
        // Must run BEFORE AddToRayIndex so the new arrow doesn't match itself.
        ulong nBit = 1UL << (nIdx & 63);
        int nWord = nIdx >> 6;
        foreach (Cell c in arrow.Cells)
        {
            int cellX = c.X,
                cellY = c.Y;
            foreach (Arrow a in _rightHeadsByRow[cellY])
                if (a.HeadCell.X < cellX && a._generationIndex >= 0)
                    SetDepBit(a._generationIndex, nWord, nBit);
            foreach (Arrow a in _leftHeadsByRow[cellY])
                if (a.HeadCell.X > cellX && a._generationIndex >= 0)
                    SetDepBit(a._generationIndex, nWord, nBit);
            foreach (Arrow a in _upHeadsByCol[cellX])
                if (a.HeadCell.Y < cellY && a._generationIndex >= 0)
                    SetDepBit(a._generationIndex, nWord, nBit);
            foreach (Arrow a in _downHeadsByCol[cellX])
                if (a.HeadCell.Y > cellY && a._generationIndex >= 0)
                    SetDepBit(a._generationIndex, nWord, nBit);
        }

        AddToRayIndex(arrow);
    }

    /// <summary>Sets a dep bit and maintains the sparse nonzero word index.</summary>
    private void SetDepBit(int arrowIdx, int word, ulong bit)
    {
        int offset = arrowIdx * _bitsetWords + word;
        bool wasZero = _depsBitsFlat[offset] == 0;
        _depsBitsFlat[offset] |= bit;
        _hasAnyDeps[arrowIdx] = true;

        if (wasZero)
        {
            int count = _depsNonZeroCount[arrowIdx];
            if (count >= 0 && count < MaxNonZeroTracked)
            {
                _depsNonZeroWords[arrowIdx * MaxNonZeroTracked + count] = word;
                _depsNonZeroCount[arrowIdx] = count + 1;
            }
            else if (count >= 0)
            {
                // Overflow: mark as -1 to signal full scan fallback
                _depsNonZeroCount[arrowIdx] = -1;
            }
        }
    }

    /// <summary>
    /// Builds the HashSet dependency graph after generation completes.
    /// Must be called (and fully exhausted) before <see cref="IsClearable"/> or <see cref="RemoveArrow"/>.
    /// Yields the number of arrows finalized so far for progress reporting.
    /// </summary>
    internal IEnumerator<int> FinalizeGenerationIncremental()
    {
        foreach (Arrow arrow in _arrows)
        {
            _dependsOn[arrow] = new HashSet<Arrow>();
            _dependedOnBy[arrow] = new HashSet<Arrow>();
        }

        int finalized = 0;
        foreach (Arrow arrow in _arrows)
        {
            (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
            int cx = arrow.HeadCell.X + dx,
                cy = arrow.HeadCell.Y + dy;
            while (cx >= 0 && cx < Width && cy >= 0 && cy < Height)
            {
                Arrow hit = _occupancy[cx, cy];
                if (hit != null && hit != arrow)
                {
                    _dependsOn[arrow].Add(hit);
                    _dependedOnBy[hit].Add(arrow);
                }
                cx += dx;
                cy += dy;
            }
            finalized++;
            yield return finalized;
        }

        _depsBitsFlat = null;
        _hasAnyDeps = null;
        _depsNonZeroWords = null;
        _depsNonZeroCount = null;
        _genHeadX = null;
        _genHeadY = null;
        _genDir = null;
        _availableArrowHeads = null;
    }

    /// <summary>
    /// Synchronous version of <see cref="FinalizeGenerationIncremental"/>. Used by tests.
    /// </summary>
    internal void FinalizeGeneration()
    {
        var iter = FinalizeGenerationIncremental();
        while (iter.MoveNext()) { }
    }

    public void RemoveArrow(Arrow arrow)
    {
        if (arrow == null)
            throw new System.ArgumentNullException(nameof(arrow));
        if (!_arrowSet.Contains(arrow))
            throw new System.InvalidOperationException("Arrow is not on the board.");
        if (!IsClearable(arrow))
            throw new System.InvalidOperationException(
                "Arrow is not clearable — it has unresolved dependencies."
            );

        _arrows.Remove(arrow);
        _arrowSet.Remove(arrow);
        foreach (Cell c in arrow.Cells)
            _occupancy[c.X, c.Y] = null;
        OccupiedCellCount -= arrow.Cells.Count;

        _dependsOn.Remove(arrow);

        // Remove reverse edges: these depended on arrow
        if (_dependedOnBy.TryGetValue(arrow, out var revDeps))
        {
            foreach (Arrow depBy in revDeps)
                _dependsOn[depBy].Remove(arrow);
            _dependedOnBy.Remove(arrow);
        }

        RemoveFromRayIndex(arrow);
    }

    public bool Contains(Cell cell)
    {
        return cell.X >= 0 && cell.X < Width && cell.Y >= 0 && cell.Y < Height;
    }

    /// <summary>Returns the arrow occupying <paramref name="cell"/>, or null if empty or out of bounds.</summary>
    public Arrow GetArrowAt(Cell cell) => Contains(cell) ? _occupancy[cell.X, cell.Y] : null;

    /// <summary>
    /// Returns the first arrow hit by walking the ray from <paramref name="arrow"/>'s head
    /// in its <see cref="Arrow.HeadDirection"/>, or null if the ray is clear.
    /// </summary>
    public Arrow GetFirstInRay(Arrow arrow)
    {
        (int dx, int dy) = Arrow.GetDirectionStep(arrow.HeadDirection);
        Cell cursor = new(arrow.HeadCell.X + dx, arrow.HeadCell.Y + dy);
        while (Contains(cursor))
        {
            Arrow hit = _occupancy[cursor.X, cursor.Y];
            if (hit != null && hit != arrow)
                return hit;
            cursor = new(cursor.X + dx, cursor.Y + dy);
        }
        return null;
    }

    /// <summary>
    /// Returns true if <paramref name="arrow"/> can be cleared — no other arrow blocks its forward ray.
    /// </summary>
    public bool IsClearable(Arrow arrow) => _dependsOn[arrow].Count == 0;

    /// <summary>Returns the set of arrows that block <paramref name="arrow"/> from being cleared.</summary>
    internal HashSet<Arrow> GetDependencies(Arrow arrow) => _dependsOn[arrow];

    /// <summary>Returns the set of arrows that depend on <paramref name="arrow"/> (i.e., arrows that
    /// become clearable candidates when <paramref name="arrow"/> is removed).</summary>
    public IReadOnlyCollection<Arrow> GetDependents(Arrow arrow) =>
        _dependedOnBy.TryGetValue(arrow, out var deps)
            ? deps
            : (IReadOnlyCollection<Arrow>)System.Array.Empty<Arrow>();

    /// <summary>Returns whether <paramref name="target"/> lies strictly forward of <paramref name="head"/> along <paramref name="direction"/>.</summary>
    public static bool IsInRay(Cell target, Cell head, Arrow.Direction direction) =>
        direction switch
        {
            Arrow.Direction.Up => target.X == head.X && target.Y > head.Y,
            Arrow.Direction.Down => target.X == head.X && target.Y < head.Y,
            Arrow.Direction.Right => target.Y == head.Y && target.X > head.X,
            Arrow.Direction.Left => target.Y == head.Y && target.X < head.X,
            _ => false,
        };

    private void AddToRayIndex(Arrow arrow)
    {
        switch (arrow.HeadDirection)
        {
            case Arrow.Direction.Right:
                _rightHeadsByRow[arrow.HeadCell.Y].Add(arrow);
                break;
            case Arrow.Direction.Left:
                _leftHeadsByRow[arrow.HeadCell.Y].Add(arrow);
                break;
            case Arrow.Direction.Up:
                _upHeadsByCol[arrow.HeadCell.X].Add(arrow);
                break;
            case Arrow.Direction.Down:
                _downHeadsByCol[arrow.HeadCell.X].Add(arrow);
                break;
        }
    }

    private void RemoveFromRayIndex(Arrow arrow)
    {
        switch (arrow.HeadDirection)
        {
            case Arrow.Direction.Right:
                _rightHeadsByRow[arrow.HeadCell.Y].Remove(arrow);
                break;
            case Arrow.Direction.Left:
                _leftHeadsByRow[arrow.HeadCell.Y].Remove(arrow);
                break;
            case Arrow.Direction.Up:
                _upHeadsByCol[arrow.HeadCell.X].Remove(arrow);
                break;
            case Arrow.Direction.Down:
                _downHeadsByCol[arrow.HeadCell.X].Remove(arrow);
                break;
        }
    }

    /// <summary>
    /// Returns true if any arrow whose forward ray passes through <paramref name="cell"/>
    /// has its bit set in the <paramref name="bitset"/>. Used by cycle detection during generation.
    /// </summary>
    internal bool AnyArrowWithRayThroughBitset(Cell cell, ulong[] bitset)
    {
        int cx = cell.X,
            cy = cell.Y;

        foreach (Arrow a in _rightHeadsByRow[cy])
        {
            int idx = a._generationIndex;
            if (a.HeadCell.X < cx && idx >= 0 && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }
        foreach (Arrow a in _leftHeadsByRow[cy])
        {
            int idx = a._generationIndex;
            if (a.HeadCell.X > cx && idx >= 0 && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }
        foreach (Arrow a in _upHeadsByCol[cx])
        {
            int idx = a._generationIndex;
            if (a.HeadCell.Y < cy && idx >= 0 && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }
        foreach (Arrow a in _downHeadsByCol[cx])
        {
            int idx = a._generationIndex;
            if (a.HeadCell.Y > cy && idx >= 0 && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }

        return false;
    }

    private List<ArrowHeadData> CreateInitialArrowHeads()
    {
        List<ArrowHeadData> arrowHeads = new();

        for (int x = 0; x < Width - 1; x++)
        for (int y = 0; y < Height; y++)
            arrowHeads.Add(
                new ArrowHeadData
                {
                    head = new(x + 1, y),
                    next = new(x, y),
                    direction = Arrow.Direction.Right,
                }
            );

        for (int x = 0; x < Width - 1; x++)
        for (int y = 0; y < Height; y++)
            arrowHeads.Add(
                new ArrowHeadData
                {
                    head = new(x, y),
                    next = new(x + 1, y),
                    direction = Arrow.Direction.Left,
                }
            );

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height - 1; y++)
            arrowHeads.Add(
                new ArrowHeadData
                {
                    head = new(x, y + 1),
                    next = new(x, y),
                    direction = Arrow.Direction.Up,
                }
            );

        for (int x = 0; x < Width; x++)
        for (int y = 0; y < Height - 1; y++)
            arrowHeads.Add(
                new ArrowHeadData
                {
                    head = new(x, y),
                    next = new(x, y + 1),
                    direction = Arrow.Direction.Down,
                }
            );

        return arrowHeads;
    }
}
