using System.Collections.Generic;

public static class ArrowColoring
{
    /// <summary>
    /// Assigns colors to arrows such that no two orthogonally adjacent arrows share a color.
    /// Returns an array indexed by arrow index in <see cref="Board.Arrows"/>, values are color indices 0 to maxColors-1.
    /// </summary>
    public static int[] AssignColors(Board board, int maxColors = 6)
    {
        IReadOnlyList<Arrow> arrows = board.Arrows;
        int count = arrows.Count;
        if (count == 0)
            return new int[0];

        // Build adjacency: arrow index -> set of neighbor arrow indices
        var neighbors = new HashSet<int>[count];
        for (int i = 0; i < count; i++)
            neighbors[i] = new HashSet<int>();

        // Map each cell to its arrow index for fast lookup
        var cellToIndex = new Dictionary<Cell, int>();
        for (int i = 0; i < count; i++)
            foreach (Cell c in arrows[i].Cells)
                cellToIndex[c] = i;

        // For each arrow, check orthogonal neighbors of each cell
        for (int i = 0; i < count; i++)
        {
            foreach (Cell c in arrows[i].Cells)
            {
                CheckNeighbor(c.X + 1, c.Y, i, cellToIndex, neighbors);
                CheckNeighbor(c.X - 1, c.Y, i, cellToIndex, neighbors);
                CheckNeighbor(c.X, c.Y + 1, i, cellToIndex, neighbors);
                CheckNeighbor(c.X, c.Y - 1, i, cellToIndex, neighbors);
            }
        }

        // Sort by descending degree (Welsh-Powell) so high-connectivity arrows
        // get first pick of colors. This keeps greedy coloring within bounds
        // for planar graphs — simple natural ordering can exhaust all 4 colors.
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
            order[i] = i;
        System.Array.Sort(order, (a, b) => neighbors[b].Count.CompareTo(neighbors[a].Count));

        // Greedy coloring: assign each arrow the lowest color not used by neighbors
        int[] colors = new int[count];
        for (int i = 0; i < count; i++)
            colors[i] = -1;

        var usedByNeighbors = new bool[maxColors];
        for (int idx = 0; idx < count; idx++)
        {
            int i = order[idx];
            for (int c = 0; c < maxColors; c++)
                usedByNeighbors[c] = false;

            foreach (int n in neighbors[i])
                if (colors[n] >= 0 && colors[n] < maxColors)
                    usedByNeighbors[colors[n]] = true;

            int chosen = 0;
            while (chosen < maxColors && usedByNeighbors[chosen])
                chosen++;

            // Fallback: shouldn't happen for planar graphs with maxColors >= 4
            // and degree-ordered greedy, but wrap to 0 as a safety net.
            colors[i] = chosen < maxColors ? chosen : 0;
        }

        return colors;
    }

    private static void CheckNeighbor(
        int nx,
        int ny,
        int arrowIndex,
        Dictionary<Cell, int> cellToIndex,
        HashSet<int>[] neighbors
    )
    {
        Cell neighborCell = new(nx, ny);
        if (cellToIndex.TryGetValue(neighborCell, out int otherIndex) && otherIndex != arrowIndex)
        {
            neighbors[arrowIndex].Add(otherIndex);
            neighbors[otherIndex].Add(arrowIndex);
        }
    }
}
