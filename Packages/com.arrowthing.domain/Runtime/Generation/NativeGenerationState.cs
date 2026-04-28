using System;

/// <summary>
/// All generation-time state stored as managed arrays, portable across
/// Unity and .NET server builds. Named "Native" for historical reasons
/// (was NativeArray-backed under Burst; reverted to managed arrays so
/// the server's ArrowThing.Domain project can share the same source).
/// </summary>
public struct NativeGenerationState
{
    public int boardWidth;
    public int boardHeight;
    public int maxArrows;
    public int bitsetWords;
    public int nextGenIndex;

    // Occupancy: flat [y * width + x], stores gen index (-1 = empty)
    public int[] occupancy;

    // Candidate pool as a manual swap-pop list (array + count)
    public NativeArrowHeadData[] candidates;
    public int candidateCount;

    // Bitset dependency storage
    public ulong[] depsBitsFlat; // [arrowIndex * bitsetWords + word]
    public bool[] hasAnyDeps;

    // Sparse nonzero word tracking
    public int[] depsNonZeroWords; // [idx * MaxNonZeroTracked + i]
    public int[] depsNonZeroCount; // [idx]

    // Arrow head/direction flat arrays
    public int[] genHeadX;
    public int[] genHeadY;
    public int[] genDir; // Arrow.Direction cast to int

    // Spatial ray index: flat arrays with per-row/col counts
    // rightByRow[row * width + i] = genIndex, rightByRowCount[row] = count
    public int[] rightByRow;
    public int[] rightByRowCount;
    public int[] leftByRow;
    public int[] leftByRowCount;
    public int[] upByCol;
    public int[] upByColCount;
    public int[] downByCol;
    public int[] downByColCount;

    // Working context (reused across candidates)
    public ulong[] ctxReachable;
    public ulong[] ctxForwardDeps;
    public ulong[] ctxFrontier;
    public bool[] ctxVisited; // flat [y * width + x]
    public int[] ctxDirOrder; // [4]

    // Scratch output for last generated arrow
    public int[] scratchCellsX;
    public int[] scratchCellsY;
    public int lastArrowCellCount;

    // Generation index of the just-placed arrow. With slot reuse via
    // freeSlots, this isn't necessarily nextGenIndex-1, so callers must read
    // this field instead of inferring from nextGenIndex.
    public int lastPlacedIndex;

    // Number of forward dependencies of the just-placed arrow that are
    // PERPENDICULAR to it (different orientation parity). Trivial collinear
    // deps don't contribute. Used by endless mode to weight garbage points
    // by how much "cross-traffic" each arrow introduces — collinear chains
    // are easy and cheap; crossings are the interesting puzzle.
    public int lastPlacedPerpDeps;

    // Free-slot reuse stack. When an arrow is removed (endless mode), its
    // generation-index slot is pushed here; PlaceArrow pops from the stack
    // before falling back to nextGenIndex++. Without this, nextGenIndex grows
    // unbounded across an endless run and overruns bitsetWords (capped at
    // maxArrows = w*h/2). Stack capacity = maxArrows: at most that many
    // simultaneously-live arrows can exist on the board.
    public int[] freeSlots;
    public int freeSlotCount;

    public NativeGenerationState(int width, int height)
    {
        boardWidth = width;
        boardHeight = height;
        maxArrows = width * height / 2;
        int estimatedCapacity = Math.Max(64, maxArrows / 4);
        bitsetWords = (estimatedCapacity + 63) >> 6;
        nextGenIndex = 0;
        lastArrowCellCount = 0;
        candidateCount = 0;

        occupancy = new int[width * height];
        for (int i = 0; i < occupancy.Length; i++)
            occupancy[i] = -1;

        // Max candidates: 2*(width-1)*height for horizontal + 2*width*(height-1) for vertical
        // Upper bound: 4 * width * height
        candidates = new NativeArrowHeadData[4 * width * height];

        depsBitsFlat = new ulong[maxArrows * bitsetWords];
        hasAnyDeps = new bool[maxArrows];
        depsNonZeroWords = new int[maxArrows * Board.MaxNonZeroTracked];
        depsNonZeroCount = new int[maxArrows];

        genHeadX = new int[maxArrows];
        genHeadY = new int[maxArrows];
        genDir = new int[maxArrows];

        // Spatial ray index: each row holds at most 'width' arrow heads per direction
        rightByRow = new int[height * width];
        rightByRowCount = new int[height];
        leftByRow = new int[height * width];
        leftByRowCount = new int[height];
        upByCol = new int[width * height];
        upByColCount = new int[width];
        downByCol = new int[width * height];
        downByColCount = new int[width];

        // Working context
        ctxReachable = new ulong[bitsetWords];
        ctxForwardDeps = new ulong[bitsetWords];
        ctxFrontier = new ulong[bitsetWords];
        ctxVisited = new bool[width * height];
        ctxDirOrder = new int[4];

        // Scratch output (max arrow length bounded by total cells)
        int maxLen = width * height;
        scratchCellsX = new int[maxLen];
        scratchCellsY = new int[maxLen];
        lastPlacedIndex = -1;
        lastPlacedPerpDeps = 0;

        freeSlots = new int[maxArrows];
        freeSlotCount = 0;
    }

    public void InitializeCandidates()
    {
        int w = boardWidth,
            h = boardHeight;
        candidateCount = 0;

        // Right-facing: head at (x+1, y), next at (x, y)
        for (int x = 0; x < w - 1; x++)
        for (int y = 0; y < h; y++)
            candidates[candidateCount++] = new NativeArrowHeadData
            {
                headX = x + 1,
                headY = y,
                nextX = x,
                nextY = y,
                direction = (int)Arrow.Direction.Right,
            };

        // Left-facing: head at (x, y), next at (x+1, y)
        for (int x = 0; x < w - 1; x++)
        for (int y = 0; y < h; y++)
            candidates[candidateCount++] = new NativeArrowHeadData
            {
                headX = x,
                headY = y,
                nextX = x + 1,
                nextY = y,
                direction = (int)Arrow.Direction.Left,
            };

        // Up-facing: head at (x, y+1), next at (x, y)
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h - 1; y++)
            candidates[candidateCount++] = new NativeArrowHeadData
            {
                headX = x,
                headY = y + 1,
                nextX = x,
                nextY = y,
                direction = (int)Arrow.Direction.Up,
            };

        // Down-facing: head at (x, y), next at (x, y+1)
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h - 1; y++)
            candidates[candidateCount++] = new NativeArrowHeadData
            {
                headX = x,
                headY = y,
                nextX = x,
                nextY = y + 1,
                direction = (int)Arrow.Direction.Down,
            };
    }

    /// <summary>
    /// Doubles bitset stride capacity, reallocating the flat dep arrays.
    /// </summary>
    public void GrowBitsetCapacity()
    {
        int oldWords = bitsetWords;
        bitsetWords = Math.Min(oldWords * 2, (maxArrows + 63) >> 6);

        var newFlat = new ulong[maxArrows * bitsetWords];
        for (int i = 0; i < nextGenIndex; i++)
        {
            Array.Copy(depsBitsFlat, i * oldWords, newFlat, i * bitsetWords, oldWords);
        }
        depsBitsFlat = newFlat;

        // Grow context arrays
        ctxReachable = new ulong[bitsetWords];
        ctxForwardDeps = new ulong[bitsetWords];
        ctxFrontier = new ulong[bitsetWords];
    }
}

/// <summary>Blittable mirror of <see cref="ArrowHeadData"/> for use in portable arrays.</summary>
public struct NativeArrowHeadData
{
    public int headX,
        headY;
    public int nextX,
        nextY;
    public int direction; // Arrow.Direction cast to int
}
