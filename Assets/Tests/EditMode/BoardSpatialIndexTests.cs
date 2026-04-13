using System.Collections.Generic;
using NUnit.Framework;

[TestFixture]
public class BoardSpatialIndexTests
{
    private static Arrow MakeArrow(params (int x, int y)[] cells)
    {
        var list = new Cell[cells.Length];
        for (int i = 0; i < cells.Length; i++)
            list[i] = new Cell(cells[i].x, cells[i].y);
        return new Arrow(list);
    }

    [Test]
    public void Add_SingleArrow_FoundInQuery()
    {
        var index = new BoardSpatialIndex(20, 20);
        var arrow = MakeArrow((3, 4), (3, 5));
        index.Add(arrow);

        var result = index.QueryRect(0, 0, 19, 19);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Contains.Item(arrow));
    }

    [Test]
    public void QueryRect_PartialOverlap_ReturnsOnlyOverlapping()
    {
        var index = new BoardSpatialIndex(40, 40);
        var arrowA = MakeArrow((2, 2), (2, 3));
        var arrowB = MakeArrow((30, 30), (30, 31));
        index.Add(arrowA);
        index.Add(arrowB);

        var result = index.QueryRect(0, 0, 15, 15);
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result, Contains.Item(arrowA));
    }

    [Test]
    public void QueryRect_EmptyRegion_ReturnsEmpty()
    {
        var index = new BoardSpatialIndex(40, 40);
        var arrow = MakeArrow((2, 2), (2, 3));
        index.Add(arrow);

        var result = index.QueryRect(20, 20, 30, 30);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Remove_ArrowNoLongerReturned()
    {
        var index = new BoardSpatialIndex(20, 20);
        var arrow = MakeArrow((5, 5), (5, 6));
        index.Add(arrow);
        index.Remove(arrow);

        var result = index.QueryRect(0, 0, 19, 19);
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void ArrowSpanningMultipleBuckets_FoundFromEitherBucket()
    {
        // BucketSize is 16, so cells at (14,0) and (17,0) span two buckets.
        var index = new BoardSpatialIndex(40, 40);
        var arrow = MakeArrow((14, 0), (15, 0), (16, 0), (17, 0));
        index.Add(arrow);

        // Query only the first bucket (0..15)
        var result1 = index.QueryRect(0, 0, 15, 15);
        Assert.That(result1, Contains.Item(arrow));

        // Query only the second bucket (16..31)
        var result2 = index.QueryRect(16, 0, 31, 15);
        Assert.That(result2, Contains.Item(arrow));
    }

    [Test]
    public void ArrowSpanningMultipleBuckets_NoDuplicatesInFullQuery()
    {
        var index = new BoardSpatialIndex(40, 40);
        var arrow = MakeArrow((14, 0), (15, 0), (16, 0), (17, 0));
        index.Add(arrow);

        var result = index.QueryRect(0, 0, 39, 39);
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void BoardBoundaryArrow_FoundInQuery()
    {
        var index = new BoardSpatialIndex(10, 10);
        var arrow = MakeArrow((9, 9), (9, 8));
        index.Add(arrow);

        var result = index.QueryRect(8, 8, 9, 9);
        Assert.That(result, Contains.Item(arrow));
    }

    [Test]
    public void SingleCellArrow_FoundInQuery()
    {
        // Arrows must have at least 2 cells in practice, but the index
        // doesn't enforce that — test with 2 cells in same bucket.
        var index = new BoardSpatialIndex(10, 10);
        var arrow = MakeArrow((5, 5), (5, 6));
        index.Add(arrow);

        var result = index.QueryRect(5, 5, 5, 5);
        Assert.That(result, Contains.Item(arrow));
    }

    [Test]
    public void MultipleArrows_QueryReturnsAll()
    {
        var index = new BoardSpatialIndex(20, 20);
        var arrows = new List<Arrow>();
        for (int i = 0; i < 10; i++)
        {
            var a = MakeArrow((i, 0), (i, 1));
            arrows.Add(a);
            index.Add(a);
        }

        var result = index.QueryRect(0, 0, 19, 19);
        Assert.That(result, Has.Count.EqualTo(10));
        foreach (var a in arrows)
            Assert.That(result, Contains.Item(a));
    }

    [Test]
    public void QueryRect_ClampedToNonNegative()
    {
        var index = new BoardSpatialIndex(10, 10);
        var arrow = MakeArrow((0, 0), (0, 1));
        index.Add(arrow);

        // Negative coords should be clamped internally.
        var result = index.QueryRect(-5, -5, 5, 5);
        Assert.That(result, Contains.Item(arrow));
    }

    [Test]
    public void QueryRect_BeyondBoard_ClampedToMax()
    {
        var index = new BoardSpatialIndex(10, 10);
        var arrow = MakeArrow((9, 9), (9, 8));
        index.Add(arrow);

        var result = index.QueryRect(0, 0, 100, 100);
        Assert.That(result, Contains.Item(arrow));
    }
}
