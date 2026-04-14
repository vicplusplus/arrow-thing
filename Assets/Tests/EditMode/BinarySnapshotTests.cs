using System.Collections.Generic;
using NUnit.Framework;

[TestFixture]
public class BinarySnapshotTests
{
    private static List<Cell> Path(params (int x, int y)[] cells)
    {
        var list = new List<Cell>(cells.Length);
        foreach (var c in cells)
            list.Add(new Cell(c.x, c.y));
        return list;
    }

    // ── Arrow data layer ──────────────────────────────────────────────

    [Test]
    public void EncodeArrows_Empty_RoundTrips()
    {
        var arrows = new List<IReadOnlyList<Cell>>();
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        var decoded = BinarySnapshot.DecodeArrows(bytes);
        Assert.That(decoded, Is.Empty);
    }

    [Test]
    public void EncodeArrows_SingleHorizontalArrow_RoundTrips()
    {
        var arrows = new List<IReadOnlyList<Cell>> { Path((3, 5), (4, 5), (5, 5)) };
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        var decoded = BinarySnapshot.DecodeArrows(bytes);
        Assert.That(decoded, Has.Count.EqualTo(1));
        CollectionAssert.AreEqual(arrows[0], decoded[0]);
    }

    [Test]
    public void EncodeArrows_AllFourDirections_RoundTrip()
    {
        var arrows = new List<IReadOnlyList<Cell>>
        {
            Path((0, 0), (0, 1)), // Up
            Path((0, 0), (1, 0)), // Right
            Path((0, 5), (0, 4)), // Down
            Path((5, 0), (4, 0)), // Left
        };
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        var decoded = BinarySnapshot.DecodeArrows(bytes);
        Assert.That(decoded, Has.Count.EqualTo(4));
        for (int i = 0; i < 4; i++)
            CollectionAssert.AreEqual(arrows[i], decoded[i]);
    }

    [Test]
    public void EncodeArrows_LShapedPath_RoundTrip()
    {
        var arrows = new List<IReadOnlyList<Cell>>
        {
            Path((0, 0), (1, 0), (2, 0), (2, 1), (2, 2)), // Right-Right-Up-Up
        };
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        var decoded = BinarySnapshot.DecodeArrows(bytes);
        CollectionAssert.AreEqual(arrows[0], decoded[0]);
    }

    [Test]
    public void EncodeArrows_Deterministic()
    {
        var arrows = new List<IReadOnlyList<Cell>>
        {
            Path((10, 20), (10, 21), (10, 22), (11, 22)),
            Path((5, 5), (6, 5)),
        };
        var a = BinarySnapshot.EncodeArrows(arrows);
        var b = BinarySnapshot.EncodeArrows(arrows);
        CollectionAssert.AreEqual(a, b);
    }

    [Test]
    public void EncodeArrows_MaxLengthArrow_RoundTrip()
    {
        // 40-cell horizontal arrow (matches typical max).
        var cells = new List<Cell>(40);
        for (int x = 0; x < 40; x++)
            cells.Add(new Cell(x, 10));
        var arrows = new List<IReadOnlyList<Cell>> { cells };
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        var decoded = BinarySnapshot.DecodeArrows(bytes);
        Assert.That(decoded[0], Has.Count.EqualTo(40));
        CollectionAssert.AreEqual(cells, decoded[0]);
    }

    [Test]
    public void EncodeArrows_LargeCount_RoundTrip()
    {
        // Stress: 1000 arrows.
        var arrows = new List<IReadOnlyList<Cell>>(1000);
        for (int i = 0; i < 1000; i++)
            arrows.Add(Path((i % 100, i / 100), (i % 100, i / 100 + 1)));
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        var decoded = BinarySnapshot.DecodeArrows(bytes);
        Assert.That(decoded, Has.Count.EqualTo(1000));
        for (int i = 0; i < 1000; i++)
            CollectionAssert.AreEqual(arrows[i], decoded[i]);
    }

    // ── Full layer (with header) ──────────────────────────────────────

    [Test]
    public void EncodeFull_RoundTrip_PreservesBoardState()
    {
        var board = new Board(20, 20);
        board.AddArrow(new Arrow(Path((5, 5), (5, 6), (5, 7))));
        board.AddArrow(new Arrow(Path((10, 10), (11, 10))));

        var bytes = BinarySnapshot.EncodeFull(board, seed: 42, maxArrowLength: 40, gzip: false);
        var decoded = BinarySnapshot.DecodeFull(bytes);

        Assert.That(decoded.Width, Is.EqualTo(20));
        Assert.That(decoded.Height, Is.EqualTo(20));
        Assert.That(decoded.Arrows, Has.Count.EqualTo(2));
        CollectionAssert.AreEqual(
            new[] { new Cell(5, 5), new Cell(5, 6), new Cell(5, 7) },
            decoded.Arrows[0].Cells
        );
    }

    [Test]
    public void EncodeFull_Gzipped_RoundTrip()
    {
        var board = new Board(50, 50);
        for (int x = 0; x < 10; x++)
            board.AddArrow(new Arrow(Path((x, 0), (x, 1), (x, 2))));

        var bytes = BinarySnapshot.EncodeFull(board, seed: 12345, maxArrowLength: 40, gzip: true);
        var decoded = BinarySnapshot.DecodeFull(bytes);

        Assert.That(decoded.Arrows, Has.Count.EqualTo(10));
    }

    [Test]
    public void PeekHeader_ReturnsCorrectMetadata()
    {
        var board = new Board(64, 32);
        board.AddArrow(new Arrow(Path((0, 0), (0, 1))));
        board.AddArrow(new Arrow(Path((10, 10), (11, 10))));

        var bytes = BinarySnapshot.EncodeFull(board, seed: 9999, maxArrowLength: 40, gzip: true);
        var (w, h, seed, count) = BinarySnapshot.PeekHeader(bytes);

        Assert.That(w, Is.EqualTo(64));
        Assert.That(h, Is.EqualTo(32));
        Assert.That(seed, Is.EqualTo(9999));
        Assert.That(count, Is.EqualTo(2));
    }
}
