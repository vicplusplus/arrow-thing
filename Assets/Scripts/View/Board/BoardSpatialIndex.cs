using System.Collections.Generic;

/// <summary>
/// Bucket-based spatial index for fast rectangle queries over arrows on a board.
/// Each bucket covers <see cref="BucketSize"/> × <see cref="BucketSize"/> cells.
/// An arrow is registered in every bucket that contains any of its cells.
/// Pure C# — no Unity dependencies.
/// </summary>
public sealed class BoardSpatialIndex
{
    public const int BucketSize = 16;

    private readonly int _bucketsX;
    private readonly int _bucketsY;
    private readonly List<Arrow>[] _buckets;

    public BoardSpatialIndex(int boardWidth, int boardHeight)
    {
        _bucketsX = (boardWidth + BucketSize - 1) / BucketSize;
        _bucketsY = (boardHeight + BucketSize - 1) / BucketSize;
        _buckets = new List<Arrow>[_bucketsX * _bucketsY];
        for (int i = 0; i < _buckets.Length; i++)
            _buckets[i] = new List<Arrow>();
    }

    public void Add(Arrow arrow)
    {
        foreach (int bi in GetBucketIndices(arrow))
            _buckets[bi].Add(arrow);
    }

    public void Remove(Arrow arrow)
    {
        foreach (int bi in GetBucketIndices(arrow))
            _buckets[bi].Remove(arrow);
    }

    /// <summary>
    /// Returns all arrows with at least one cell in the given cell-coordinate rectangle (inclusive).
    /// </summary>
    public HashSet<Arrow> QueryRect(int minX, int minY, int maxX, int maxY)
    {
        var result = new HashSet<Arrow>();

        int bMinX = minX / BucketSize;
        int bMinY = minY / BucketSize;
        int bMaxX = maxX / BucketSize;
        int bMaxY = maxY / BucketSize;

        if (bMinX < 0)
            bMinX = 0;
        if (bMinY < 0)
            bMinY = 0;
        if (bMaxX >= _bucketsX)
            bMaxX = _bucketsX - 1;
        if (bMaxY >= _bucketsY)
            bMaxY = _bucketsY - 1;

        for (int by = bMinY; by <= bMaxY; by++)
        {
            for (int bx = bMinX; bx <= bMaxX; bx++)
            {
                var bucket = _buckets[by * _bucketsX + bx];
                for (int i = 0; i < bucket.Count; i++)
                    result.Add(bucket[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the set of unique bucket indices that the arrow's cells fall into.
    /// </summary>
    private HashSet<int> GetBucketIndices(Arrow arrow)
    {
        var indices = new HashSet<int>();
        foreach (Cell c in arrow.Cells)
        {
            int bx = c.X / BucketSize;
            int by = c.Y / BucketSize;
            indices.Add(by * _bucketsX + bx);
        }
        return indices;
    }
}
