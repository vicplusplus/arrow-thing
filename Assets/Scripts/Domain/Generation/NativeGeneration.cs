using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Portable board generation kernels. Operates on <see cref="NativeGenerationState"/>
/// via managed arrays. Shared between Unity and the .NET server build.
///
/// Performance notes:
/// - Hot paths are marked [MethodImpl(AggressiveInlining)] to hint the JIT.
/// - Bit-twiddling uses a De Bruijn trailing-zero-count (portable across
///   netstandard2.1 and Unity; System.Numerics.BitOperations is net5.0+).
/// - All state is flat arrays; per-iteration allocations are zero.
/// </summary>
public static class NativeGeneration
{
    private const int MinArrowLength = 2;

    // De Bruijn CTZ table for 64-bit values. Portable alternative to
    // System.Numerics.BitOperations.TrailingZeroCount which requires net5.0+.
    private static readonly int[] DeBruijn64Tab =
    {
        0,
        1,
        56,
        2,
        57,
        49,
        28,
        3,
        61,
        58,
        42,
        50,
        38,
        29,
        17,
        4,
        62,
        47,
        59,
        36,
        45,
        43,
        51,
        22,
        53,
        39,
        33,
        30,
        24,
        18,
        12,
        5,
        63,
        55,
        48,
        27,
        60,
        41,
        37,
        16,
        46,
        35,
        44,
        21,
        52,
        32,
        23,
        11,
        54,
        26,
        40,
        15,
        34,
        20,
        31,
        10,
        25,
        14,
        19,
        9,
        13,
        8,
        7,
        6,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Ctz64(ulong value)
    {
        if (value == 0)
            return 64;
        ulong isolated = value & (ulong)(-(long)value); // isolate lowest set bit
        return DeBruijn64Tab[(isolated * 0x03F79D71B4CA8B09UL) >> 58];
    }

    /// <summary>
    /// Attempts to generate one arrow. On success, the arrow's cells are written to
    /// <c>state.scratchCellsX/Y[0..state.lastArrowCellCount]</c> and the arrow is
    /// placed into native state (occupancy, bitsets, ray index). Returns true on success.
    ///
    /// <paramref name="centermostFirst"/> selects the head candidate by minimum
    /// Chebyshev distance from the board center instead of uniformly at random.
    /// Ties are broken uniformly at random. Produces a square-ring fill pattern.
    /// </summary>
    public static bool TryGenerateArrow(
        ref NativeGenerationState state,
        int maxLength,
        ref PortableRandom rng,
        bool centermostFirst = false
    )
    {
        int targetLength = rng.NextInt(MinArrowLength, maxLength + 1);
        int w = state.boardWidth;

        while (state.candidateCount > 0)
        {
            int headIndex = centermostFirst
                ? SelectCentermostCandidate(ref state, ref rng)
                : rng.NextInt(state.candidateCount);
            NativeArrowHeadData candidate = state.candidates[headIndex];

            // Reject if head or next occupied
            if (
                state.occupancy[candidate.headY * w + candidate.headX] >= 0
                || state.occupancy[candidate.nextY * w + candidate.nextX] >= 0
            )
            {
                SwapRemoveCandidates(ref state, headIndex);
                continue;
            }

            // Compute forward deps as bitset
            int activeWords = (state.nextGenIndex + 63) >> 6;
            ClearWords(state.ctxForwardDeps, activeWords);
            ClearWords(state.ctxReachable, activeWords);
            int depCount = ComputeForwardDeps(
                ref state,
                candidate.headX,
                candidate.headY,
                candidate.direction
            );

            bool hasReachable = depCount > 0;
            if (hasReachable)
            {
                // Check if any forward dep has deps — if all are leaves, skip BFS
                bool needBFS = false;
                for (int iw = 0; iw < activeWords && !needBFS; iw++)
                {
                    ulong bits = state.ctxForwardDeps[iw];
                    while (bits != 0)
                    {
                        int bit = Ctz64(bits);
                        if (state.hasAnyDeps[(iw << 6) | bit])
                        {
                            needBFS = true;
                            break;
                        }
                        bits &= bits - 1;
                    }
                }

                bool hasCycle;
                if (needBFS)
                {
                    hasCycle = ComputeReachableSetEarlyAbort(
                        ref state,
                        candidate.headX,
                        candidate.headY,
                        candidate.nextX,
                        candidate.nextY,
                        activeWords
                    );
                }
                else
                {
                    // Reachable = forward deps (all leaves, no BFS needed)
                    for (int iw = 0; iw < activeWords; iw++)
                        state.ctxReachable[iw] = state.ctxForwardDeps[iw];
                    hasCycle =
                        AnyArrowWithRayThroughBitset(
                            ref state,
                            candidate.headX,
                            candidate.headY,
                            state.ctxReachable
                        )
                        || AnyArrowWithRayThroughBitset(
                            ref state,
                            candidate.nextX,
                            candidate.nextY,
                            state.ctxReachable
                        );
                }

                if (hasCycle)
                {
                    SwapRemoveCandidates(ref state, headIndex);
                    continue;
                }
            }

            int cellCount = GreedyWalk(ref state, targetLength, candidate, ref rng, hasReachable);
            if (cellCount < MinArrowLength)
            {
                SwapRemoveCandidates(ref state, headIndex);
                continue;
            }

            // Place arrow in native state
            PlaceArrow(ref state, cellCount, candidate.direction);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Greedy random walk to build an arrow tail. Writes cells to state scratch buffers.
    /// Returns the number of cells in the path.
    /// </summary>
    private static int GreedyWalk(
        ref NativeGenerationState state,
        int targetLength,
        NativeArrowHeadData headData,
        ref PortableRandom rng,
        bool hasReachable
    )
    {
        int w = state.boardWidth,
            h = state.boardHeight;

        // Initialize path with head and next
        state.scratchCellsX[0] = headData.headX;
        state.scratchCellsY[0] = headData.headY;
        state.scratchCellsX[1] = headData.nextX;
        state.scratchCellsY[1] = headData.nextY;
        int pathLen = 2;

        state.ctxVisited[headData.headY * w + headData.headX] = true;
        state.ctxVisited[headData.nextY * w + headData.nextX] = true;

        // Pre-mark ray cells in visited
        GetDirectionStep(headData.direction, out int rdx, out int rdy);
        int rx = headData.headX + rdx,
            ry = headData.headY + rdy;
        while (rx >= 0 && rx < w && ry >= 0 && ry < h)
        {
            state.ctxVisited[ry * w + rx] = true;
            rx += rdx;
            ry += rdy;
        }

        int curX = headData.nextX,
            curY = headData.nextY;

        while (pathLen < targetLength)
        {
            // Fisher-Yates shuffle on 4 directions
            state.ctxDirOrder[0] = 0;
            state.ctxDirOrder[1] = 1;
            state.ctxDirOrder[2] = 2;
            state.ctxDirOrder[3] = 3;
            for (int i = 3; i > 0; i--)
            {
                int j = rng.NextInt(0, i + 1);
                int tmp = state.ctxDirOrder[i];
                state.ctxDirOrder[i] = state.ctxDirOrder[j];
                state.ctxDirOrder[j] = tmp;
            }

            bool found = false;
            for (int i = 0; i < 4; i++)
            {
                int nx,
                    ny;
                switch (state.ctxDirOrder[i])
                {
                    case 0:
                        nx = curX + 1;
                        ny = curY;
                        break;
                    case 1:
                        nx = curX - 1;
                        ny = curY;
                        break;
                    case 2:
                        nx = curX;
                        ny = curY + 1;
                        break;
                    default:
                        nx = curX;
                        ny = curY - 1;
                        break;
                }

                if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                    continue;
                int flatIdx = ny * w + nx;
                if (state.ctxVisited[flatIdx])
                    continue;
                if (state.occupancy[flatIdx] >= 0)
                    continue;
                if (
                    hasReachable
                    && AnyArrowWithRayThroughBitset(ref state, nx, ny, state.ctxReachable)
                )
                    continue;

                state.scratchCellsX[pathLen] = nx;
                state.scratchCellsY[pathLen] = ny;
                pathLen++;
                state.ctxVisited[flatIdx] = true;
                curX = nx;
                curY = ny;
                found = true;
                break;
            }

            if (!found)
                break;
        }

        // Clean up visited: path cells
        for (int i = 0; i < pathLen; i++)
            state.ctxVisited[state.scratchCellsY[i] * w + state.scratchCellsX[i]] = false;
        // Clean up visited: ray cells
        rx = headData.headX + rdx;
        ry = headData.headY + rdy;
        while (rx >= 0 && rx < w && ry >= 0 && ry < h)
        {
            state.ctxVisited[ry * w + rx] = false;
            rx += rdx;
            ry += rdy;
        }

        return pathLen;
    }

    /// <summary>
    /// Collects all distinct arrows in the forward ray into the ctxForwardDeps bitset.
    /// Returns the count.
    /// </summary>
    private static int ComputeForwardDeps(
        ref NativeGenerationState state,
        int headX,
        int headY,
        int direction
    )
    {
        int count = 0;
        GetDirectionStep(direction, out int dx, out int dy);
        int cx = headX + dx,
            cy = headY + dy;
        int w = state.boardWidth,
            h = state.boardHeight;

        while (cx >= 0 && cx < w && cy >= 0 && cy < h)
        {
            int hitIdx = state.occupancy[cy * w + cx];
            if (hitIdx >= 0)
            {
                int word = hitIdx >> 6;
                ulong bit = 1UL << (hitIdx & 63);
                if ((state.ctxForwardDeps[word] & bit) == 0)
                {
                    state.ctxForwardDeps[word] = state.ctxForwardDeps[word] | bit;
                    count++;
                }
            }
            cx += dx;
            cy += dy;
        }
        return count;
    }

    /// <summary>
    /// BFS transitive closure with inline early cycle detection.
    /// Returns true if any reachable arrow's ray crosses head or next (cycle).
    /// </summary>
    private static bool ComputeReachableSetEarlyAbort(
        ref NativeGenerationState state,
        int headX,
        int headY,
        int nextX,
        int nextY,
        int activeWords
    )
    {
        int stride = state.bitsetWords;

        for (int iw = 0; iw < activeWords; iw++)
        {
            state.ctxReachable[iw] = state.ctxForwardDeps[iw];
            state.ctxFrontier[iw] = state.ctxForwardDeps[iw];
        }

        // Check level 0 (forward deps themselves)
        if (
            AnyArrowWithRayThroughBitset(ref state, headX, headY, state.ctxReachable)
            || AnyArrowWithRayThroughBitset(ref state, nextX, nextY, state.ctxReachable)
        )
            return true;

        while (true)
        {
            bool hasNext = false;
            for (int iw = 0; iw < activeWords; iw++)
            {
                ulong bits = state.ctxFrontier[iw];
                if (bits == 0)
                    continue;
                state.ctxFrontier[iw] = 0;

                while (bits != 0)
                {
                    int bit = Ctz64(bits);
                    int idx = (iw << 6) | bit;
                    int offset = idx * stride;
                    int nzCount = state.depsNonZeroCount[idx];

                    if (nzCount >= 0 && nzCount <= Board.MaxNonZeroTracked)
                    {
                        int nzBase = idx * Board.MaxNonZeroTracked;
                        for (int i = 0; i < nzCount; i++)
                        {
                            int ww = state.depsNonZeroWords[nzBase + i];
                            ulong newBits =
                                state.depsBitsFlat[offset + ww] & ~state.ctxReachable[ww];
                            if (newBits != 0)
                            {
                                state.ctxReachable[ww] = state.ctxReachable[ww] | newBits;
                                state.ctxFrontier[ww] = state.ctxFrontier[ww] | newBits;
                                hasNext = true;
                                if (
                                    CheckNewBitsForCycle(
                                        ref state,
                                        newBits,
                                        ww,
                                        headX,
                                        headY,
                                        nextX,
                                        nextY
                                    )
                                )
                                    return true;
                            }
                        }
                    }
                    else
                    {
                        for (int ww = 0; ww < activeWords; ww++)
                        {
                            ulong newBits =
                                state.depsBitsFlat[offset + ww] & ~state.ctxReachable[ww];
                            if (newBits != 0)
                            {
                                state.ctxReachable[ww] = state.ctxReachable[ww] | newBits;
                                state.ctxFrontier[ww] = state.ctxFrontier[ww] | newBits;
                                hasNext = true;
                                if (
                                    CheckNewBitsForCycle(
                                        ref state,
                                        newBits,
                                        ww,
                                        headX,
                                        headY,
                                        nextX,
                                        nextY
                                    )
                                )
                                    return true;
                            }
                        }
                    }
                    bits &= bits - 1;
                }
            }

            if (!hasNext)
                break;
        }

        return false;
    }

    /// <summary>
    /// Checks each newly discovered arrow in the BFS for a cycle
    /// (its ray crosses head or next).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CheckNewBitsForCycle(
        ref NativeGenerationState state,
        ulong newBits,
        int word,
        int headX,
        int headY,
        int nextX,
        int nextY
    )
    {
        while (newBits != 0)
        {
            int b = Ctz64(newBits);
            int newIdx = (word << 6) | b;
            if (
                IsInRayOf(
                    state.genHeadX[newIdx],
                    state.genHeadY[newIdx],
                    state.genDir[newIdx],
                    headX,
                    headY
                )
                || IsInRayOf(
                    state.genHeadX[newIdx],
                    state.genHeadY[newIdx],
                    state.genDir[newIdx],
                    nextX,
                    nextY
                )
            )
                return true;
            newBits &= newBits - 1;
        }
        return false;
    }

    /// <summary>
    /// Returns true if any arrow in the bitset has a ray passing through (cx, cy).
    /// Uses the spatial ray index for efficient lookup.
    /// </summary>
    private static bool AnyArrowWithRayThroughBitset(
        ref NativeGenerationState state,
        int cx,
        int cy,
        ulong[] bitset
    )
    {
        int w = state.boardWidth;

        // Right-facing arrows on this row with headX < cx
        int rowBase = cy * w;
        int count = state.rightByRowCount[cy];
        for (int i = 0; i < count; i++)
        {
            int idx = state.rightByRow[rowBase + i];
            if (state.genHeadX[idx] < cx && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }

        // Left-facing arrows on this row with headX > cx
        count = state.leftByRowCount[cy];
        for (int i = 0; i < count; i++)
        {
            int idx = state.leftByRow[rowBase + i];
            if (state.genHeadX[idx] > cx && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }

        // Up-facing arrows on this column with headY < cy
        int colBase = cx * state.boardHeight;
        count = state.upByColCount[cx];
        for (int i = 0; i < count; i++)
        {
            int idx = state.upByCol[colBase + i];
            if (state.genHeadY[idx] < cy && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }

        // Down-facing arrows on this column with headY > cy
        count = state.downByColCount[cx];
        for (int i = 0; i < count; i++)
        {
            int idx = state.downByCol[colBase + i];
            if (state.genHeadY[idx] > cy && (bitset[idx >> 6] & (1UL << (idx & 63))) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Full board-solvability check via Kahn's topological sort over the
    /// native dep-bitset graph. Returns true iff every live arrow can be
    /// drained (no cycle).
    ///
    /// Used as a post-spawn safety net for endless mode: even if the
    /// incremental cycle detector inside <see cref="TryGenerateArrow"/>
    /// misses an edge case, this catches it. O(n * stride + n²) where n =
    /// nextGenIndex; for the 20×20 endless board (n &lt;= ~200) this is on
    /// the order of tens of thousands of ops — negligible per spawn.
    ///
    /// Dead slots (freed via <see cref="RemoveArrow"/>) have zero dep
    /// rows AND no other arrow depends on them, so they are processed
    /// immediately and don't affect the result.
    /// </summary>
    public static bool IsBoardSolvable(ref NativeGenerationState state)
    {
        int n = state.nextGenIndex;
        if (n == 0)
            return true;

        int stride = state.bitsetWords;
        int activeWords = (n + 63) >> 6;
        if (activeWords > stride)
            activeWords = stride;

        var inDeg = new int[n];
        for (int i = 0; i < n; i++)
        {
            int offset = i * stride;
            int count = 0;
            for (int w = 0; w < activeWords; w++)
            {
                ulong bits = state.depsBitsFlat[offset + w];
                while (bits != 0)
                {
                    bits &= bits - 1;
                    count++;
                }
            }
            inDeg[i] = count;
        }

        var queue = new System.Collections.Generic.Queue<int>(n);
        for (int i = 0; i < n; i++)
            if (inDeg[i] == 0)
                queue.Enqueue(i);

        int processed = 0;
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            processed++;

            // Decrement in-degree of every arrow whose dep row contains idx.
            int word = idx >> 6;
            ulong mask = 1UL << (idx & 63);
            for (int j = 0; j < n; j++)
            {
                int off = j * stride + word;
                if ((state.depsBitsFlat[off] & mask) != 0)
                {
                    inDeg[j]--;
                    if (inDeg[j] == 0)
                        queue.Enqueue(j);
                }
            }
        }

        return processed == n;
    }

    /// <summary>
    /// Removes a previously-placed arrow from native state: clears occupancy,
    /// removes from ray index, zeros the arrow's dep-row bitset, and clears
    /// the arrow's bit from every other arrow's dep row. Also rebuilds the
    /// candidate pool so head positions whose cells just freed re-enter
    /// candidacy (without this, the pool monotonically shrinks across the
    /// life of an endless run, eventually starving spawns even on a mostly-
    /// empty board).
    ///
    /// Used by the endless-mode session to take a cleared real arrow out of
    /// the live generation cache, so subsequent pending spawns don't see it
    /// as a blocker AND can re-use its cells. The generation index slot
    /// becomes dead — <c>nextGenIndex</c> is not rewound, so the slot is
    /// simply unused (matches the pattern in
    /// <see cref="Board.RemoveArrowForGeneration"/>).
    ///
    /// Since removal only ever prunes dependencies (never creates them), no
    /// other arrow's cycle-reachability data becomes invalid as a result of
    /// this call — the dep state is strictly monotonically shrinking between
    /// spawns. The candidate pool is the one piece that needs to grow back.
    /// </summary>
    public static void RemoveArrow(
        ref NativeGenerationState state,
        int genIndex,
        IReadOnlyList<Cell> cells
    )
    {
        int w = state.boardWidth;

        // Clear occupancy
        for (int i = 0; i < cells.Count; i++)
            state.occupancy[cells[i].Y * w + cells[i].X] = -1;

        // Remove from ray index
        int headX = state.genHeadX[genIndex];
        int headY = state.genHeadY[genIndex];
        int dir = state.genDir[genIndex];
        RemoveFromRayIndex(ref state, genIndex, headX, headY, dir);

        // Zero the arrow's dep row bitset
        int stride = state.bitsetWords;
        int baseOffset = genIndex * stride;
        for (int i = 0; i < stride; i++)
            state.depsBitsFlat[baseOffset + i] = 0;
        state.hasAnyDeps[genIndex] = false;
        state.depsNonZeroCount[genIndex] = 0;

        // Clear the arrow's bit from every other arrow's dep row
        int word = genIndex >> 6;
        ulong mask = ~(1UL << (genIndex & 63));
        for (int i = 0; i < state.nextGenIndex; i++)
            state.depsBitsFlat[i * stride + word] &= mask;

        // Recycle the slot. Endless mode reuses gen indices via this stack so
        // nextGenIndex stays bounded by max simultaneously-live arrows.
        state.freeSlots[state.freeSlotCount++] = genIndex;

        // Rebuild candidate pool. Costs O(W*H) — trivial on the 20×20 endless
        // board. Lazy occupancy/cycle pruning inside TryGenerateArrow handles
        // the cleanup as candidates are sampled.
        state.InitializeCandidates();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RemoveFromRayIndex(
        ref NativeGenerationState state,
        int genIdx,
        int headX,
        int headY,
        int direction
    )
    {
        int w = state.boardWidth,
            h = state.boardHeight;
        switch (direction)
        {
            case 0: // Up
            {
                int col = headX;
                int count = state.upByColCount[col];
                int colBase = col * h;
                for (int i = 0; i < count; i++)
                {
                    if (state.upByCol[colBase + i] == genIdx)
                    {
                        state.upByCol[colBase + i] = state.upByCol[colBase + count - 1];
                        state.upByColCount[col] = count - 1;
                        return;
                    }
                }
                break;
            }
            case 1: // Right
            {
                int row = headY;
                int count = state.rightByRowCount[row];
                int rowBase = row * w;
                for (int i = 0; i < count; i++)
                {
                    if (state.rightByRow[rowBase + i] == genIdx)
                    {
                        state.rightByRow[rowBase + i] = state.rightByRow[rowBase + count - 1];
                        state.rightByRowCount[row] = count - 1;
                        return;
                    }
                }
                break;
            }
            case 2: // Down
            {
                int col = headX;
                int count = state.downByColCount[col];
                int colBase = col * h;
                for (int i = 0; i < count; i++)
                {
                    if (state.downByCol[colBase + i] == genIdx)
                    {
                        state.downByCol[colBase + i] = state.downByCol[colBase + count - 1];
                        state.downByColCount[col] = count - 1;
                        return;
                    }
                }
                break;
            }
            case 3: // Left
            {
                int row = headY;
                int count = state.leftByRowCount[row];
                int rowBase = row * w;
                for (int i = 0; i < count; i++)
                {
                    if (state.leftByRow[rowBase + i] == genIdx)
                    {
                        state.leftByRow[rowBase + i] = state.leftByRow[rowBase + count - 1];
                        state.leftByRowCount[row] = count - 1;
                        return;
                    }
                }
                break;
            }
        }
    }

    /// <summary>
    /// Places an arrow with explicit cells into native state. Used by the endless
    /// session to reconstruct native state from a finalized Board at the start of
    /// a run (the Board's own generation-time arrays have been nulled by
    /// <see cref="Board.FinalizeGenerationIncremental"/>, so the native state
    /// must be rebuilt in order to support further spawns).
    ///
    /// Copies cells into the state's scratch buffers, then calls the same
    /// placement logic used during the initial fill loop.
    /// </summary>
    public static void PlaceArrowExplicit(
        ref NativeGenerationState state,
        IReadOnlyList<Cell> cells,
        int direction
    )
    {
        for (int i = 0; i < cells.Count; i++)
        {
            state.scratchCellsX[i] = cells[i].X;
            state.scratchCellsY[i] = cells[i].Y;
        }
        PlaceArrow(ref state, cells.Count, direction);
    }

    /// <summary>
    /// Places a generated arrow into native state: occupancy, bitsets, ray index.
    /// Reads cells from scratchCellsX/Y[0..cellCount].
    /// </summary>
    private static void PlaceArrow(ref NativeGenerationState state, int cellCount, int direction)
    {
        // Reuse a freed slot if any (endless-mode clear path pushes here).
        // Otherwise allocate the next sequential index. Without this, an
        // endless run grows nextGenIndex unbounded and overruns bitsetWords
        // (capped at maxArrows).
        int nIdx;
        if (state.freeSlotCount > 0)
        {
            nIdx = state.freeSlots[--state.freeSlotCount];
        }
        else
        {
            nIdx = state.nextGenIndex++;
            if (nIdx >= state.bitsetWords * 64)
                state.GrowBitsetCapacity();
        }

        int w = state.boardWidth,
            h = state.boardHeight;
        int headX = state.scratchCellsX[0];
        int headY = state.scratchCellsY[0];

        state.genHeadX[nIdx] = headX;
        state.genHeadY[nIdx] = headY;
        state.genDir[nIdx] = direction;

        // Set occupancy
        for (int i = 0; i < cellCount; i++)
            state.occupancy[state.scratchCellsY[i] * w + state.scratchCellsX[i]] = nIdx;

        // Forward deps: walk ray, set bits. Also count perpendicular deps
        // for endless-mode garbage weighting (collinear deps are trivial,
        // perpendicular deps are the interesting puzzle).
        bool hasDeps = false;
        int perpDeps = 0;
        int dirParity = direction & 1; // 0 = vertical (Up/Down), 1 = horizontal (Left/Right)
        GetDirectionStep(direction, out int dx, out int dy);
        int cx = headX + dx,
            cy = headY + dy;
        while (cx >= 0 && cx < w && cy >= 0 && cy < h)
        {
            int hitIdx = state.occupancy[cy * w + cx];
            if (hitIdx >= 0 && hitIdx != nIdx)
            {
                int dWord = hitIdx >> 6;
                SetDepBit(ref state, nIdx, dWord, 1UL << (hitIdx & 63));
                hasDeps = true;
                if ((state.genDir[hitIdx] & 1) != dirParity)
                    perpDeps++;
            }
            cx += dx;
            cy += dy;
        }
        state.hasAnyDeps[nIdx] = hasDeps;
        state.lastPlacedPerpDeps = perpDeps;

        // Reverse deps: existing arrows whose rays cross this arrow's cells
        ulong nBit = 1UL << (nIdx & 63);
        int nWord = nIdx >> 6;
        for (int i = 0; i < cellCount; i++)
        {
            int cellX = state.scratchCellsX[i];
            int cellY = state.scratchCellsY[i];

            int rowBase = cellY * w;
            int count = state.rightByRowCount[cellY];
            for (int j = 0; j < count; j++)
            {
                int aIdx = state.rightByRow[rowBase + j];
                if (state.genHeadX[aIdx] < cellX)
                    SetDepBit(ref state, aIdx, nWord, nBit);
            }

            count = state.leftByRowCount[cellY];
            for (int j = 0; j < count; j++)
            {
                int aIdx = state.leftByRow[rowBase + j];
                if (state.genHeadX[aIdx] > cellX)
                    SetDepBit(ref state, aIdx, nWord, nBit);
            }

            int colBase = cellX * h;
            count = state.upByColCount[cellX];
            for (int j = 0; j < count; j++)
            {
                int aIdx = state.upByCol[colBase + j];
                if (state.genHeadY[aIdx] < cellY)
                    SetDepBit(ref state, aIdx, nWord, nBit);
            }

            count = state.downByColCount[cellX];
            for (int j = 0; j < count; j++)
            {
                int aIdx = state.downByCol[colBase + j];
                if (state.genHeadY[aIdx] > cellY)
                    SetDepBit(ref state, aIdx, nWord, nBit);
            }
        }

        // Add to ray index
        AddToRayIndex(ref state, nIdx, headX, headY, direction);

        state.lastArrowCellCount = cellCount;
        state.lastPlacedIndex = nIdx;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetDepBit(
        ref NativeGenerationState state,
        int arrowIdx,
        int word,
        ulong bit
    )
    {
        int offset = arrowIdx * state.bitsetWords + word;
        bool wasZero = state.depsBitsFlat[offset] == 0;
        state.depsBitsFlat[offset] = state.depsBitsFlat[offset] | bit;
        state.hasAnyDeps[arrowIdx] = true;

        if (wasZero)
        {
            int count = state.depsNonZeroCount[arrowIdx];
            if (count >= 0 && count < Board.MaxNonZeroTracked)
            {
                state.depsNonZeroWords[arrowIdx * Board.MaxNonZeroTracked + count] = word;
                state.depsNonZeroCount[arrowIdx] = count + 1;
            }
            else if (count >= 0)
            {
                state.depsNonZeroCount[arrowIdx] = -1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddToRayIndex(
        ref NativeGenerationState state,
        int genIdx,
        int headX,
        int headY,
        int direction
    )
    {
        int w = state.boardWidth,
            h = state.boardHeight;
        switch (direction)
        {
            case 0: // Up
            {
                int col = headX;
                int count = state.upByColCount[col];
                state.upByCol[col * h + count] = genIdx;
                state.upByColCount[col] = count + 1;
                break;
            }
            case 1: // Right
            {
                int row = headY;
                int count = state.rightByRowCount[row];
                state.rightByRow[row * w + count] = genIdx;
                state.rightByRowCount[row] = count + 1;
                break;
            }
            case 2: // Down
            {
                int col = headX;
                int count = state.downByColCount[col];
                state.downByCol[col * h + count] = genIdx;
                state.downByColCount[col] = count + 1;
                break;
            }
            case 3: // Left
            {
                int row = headY;
                int count = state.leftByRowCount[row];
                state.leftByRow[row * w + count] = genIdx;
                state.leftByRowCount[row] = count + 1;
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInRayOf(int ax, int ay, int dir, int cx, int cy)
    {
        switch (dir)
        {
            case 0:
                return cx == ax && cy > ay; // Up
            case 1:
                return cy == ay && cx > ax; // Right
            case 2:
                return cx == ax && cy < ay; // Down
            case 3:
                return cy == ay && cx < ax; // Left
            default:
                return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GetDirectionStep(int direction, out int dx, out int dy)
    {
        switch (direction)
        {
            case 0:
                dx = 0;
                dy = 1;
                break; // Up
            case 1:
                dx = 1;
                dy = 0;
                break; // Right
            case 2:
                dx = 0;
                dy = -1;
                break; // Down
            case 3:
                dx = -1;
                dy = 0;
                break; // Left
            default:
                dx = 0;
                dy = 0;
                break;
        }
    }

    /// <summary>
    /// Scans the candidate pool for the minimum Chebyshev distance from the board
    /// center (integer "twice-Chebyshev" — avoids fractional center for even-sized
    /// boards), then picks one candidate uniformly at random among ties.
    ///
    /// O(N) per call in candidate count. Not a hot path for endless-mode spawning
    /// (one arrow per spawn tick), and acceptable for initial fill: on a 20x20
    /// board the candidate pool is ~1600 entries and fills complete in under 100
    /// arrows, so total work is bounded.
    /// </summary>
    private static int SelectCentermostCandidate(
        ref NativeGenerationState state,
        ref PortableRandom rng
    )
    {
        int c2x = state.boardWidth - 1;
        int c2y = state.boardHeight - 1;

        // First pass: find minimum twice-Chebyshev distance, count ties.
        int minDist = int.MaxValue;
        int tieCount = 0;
        for (int i = 0; i < state.candidateCount; i++)
        {
            NativeArrowHeadData c = state.candidates[i];
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
                tieCount = 1;
            }
            else if (d == minDist)
            {
                tieCount++;
            }
        }

        // Second pass: pick the kth tied candidate.
        int k = rng.NextInt(tieCount);
        int seen = 0;
        for (int i = 0; i < state.candidateCount; i++)
        {
            NativeArrowHeadData c = state.candidates[i];
            int dx = (c.headX << 1) - c2x;
            int dy = (c.headY << 1) - c2y;
            if (dx < 0)
                dx = -dx;
            if (dy < 0)
                dy = -dy;
            int d = dx > dy ? dx : dy;
            if (d == minDist)
            {
                if (seen == k)
                    return i;
                seen++;
            }
        }

        // Unreachable: tieCount > 0 guarantees a match.
        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapRemoveCandidates(ref NativeGenerationState state, int index)
    {
        int last = --state.candidateCount;
        if (index != last)
            state.candidates[index] = state.candidates[last];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearWords(ulong[] arr, int count)
    {
        for (int i = 0; i < count; i++)
            arr[i] = 0;
    }
}
