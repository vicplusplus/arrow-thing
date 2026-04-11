using System.Collections.Generic;
using NUnit.Framework;

[TestFixture]
public class ArrowColoringTests
{
    [Test]
    public void AssignColors_NoAdjacentArrowsShareColor([Values(7, 42, 99, 123, 256)] int seed)
    {
        var board = new Board(10, 10);
        TestBoardHelper.FillBoard(board, 5, seed);

        int[] colors = ArrowColoring.AssignColors(board);

        // Build cell-to-arrow-index map
        var cellToIndex = new Dictionary<Cell, int>();
        for (int i = 0; i < board.Arrows.Count; i++)
            foreach (Cell c in board.Arrows[i].Cells)
                cellToIndex[c] = i;

        // Check every orthogonal neighbor pair
        for (int i = 0; i < board.Arrows.Count; i++)
        {
            foreach (Cell c in board.Arrows[i].Cells)
            {
                Cell[] neighbors =
                {
                    new(c.X + 1, c.Y),
                    new(c.X - 1, c.Y),
                    new(c.X, c.Y + 1),
                    new(c.X, c.Y - 1),
                };
                foreach (Cell n in neighbors)
                {
                    if (cellToIndex.TryGetValue(n, out int j) && j != i)
                    {
                        Assert.That(
                            colors[i],
                            Is.Not.EqualTo(colors[j]),
                            $"Arrows {i} and {j} are adjacent but share color {colors[i]}."
                        );
                    }
                }
            }
        }
    }

    [Test]
    public void AssignColors_AllColorsWithinRange()
    {
        var board = new Board(10, 10);
        TestBoardHelper.FillBoard(board, 5, 42);

        int maxColors = 4;
        int[] colors = ArrowColoring.AssignColors(board, maxColors);

        for (int i = 0; i < colors.Length; i++)
        {
            Assert.That(colors[i], Is.GreaterThanOrEqualTo(0));
            Assert.That(colors[i], Is.LessThan(maxColors));
        }
    }

    [Test]
    public void AssignColors_EmptyBoard_ReturnsEmpty()
    {
        var board = new Board(5, 5);
        int[] colors = ArrowColoring.AssignColors(board);
        Assert.That(colors, Is.Empty);
    }

    [Test]
    public void AssignColors_SingleArrow_ReturnsColorZero()
    {
        var board = new Board(5, 5);
        board.AddArrow(new Arrow(new[] { new Cell(0, 0), new Cell(1, 0) }));

        int[] colors = ArrowColoring.AssignColors(board);

        Assert.That(colors.Length, Is.EqualTo(1));
        Assert.That(colors[0], Is.EqualTo(0));
    }

    [Test]
    public void AssignColors_Deterministic()
    {
        var board = new Board(10, 10);
        TestBoardHelper.FillBoard(board, 5, 42);

        int[] first = ArrowColoring.AssignColors(board);
        int[] second = ArrowColoring.AssignColors(board);

        Assert.That(second, Is.EqualTo(first));
    }

    [Test]
    public void AssignColors_LargeBoard_NoAdjacentConflicts()
    {
        var board = new Board(20, 20);
        TestBoardHelper.FillBoard(board, 10, 77);

        int[] colors = ArrowColoring.AssignColors(board);

        var cellToIndex = new Dictionary<Cell, int>();
        for (int i = 0; i < board.Arrows.Count; i++)
            foreach (Cell c in board.Arrows[i].Cells)
                cellToIndex[c] = i;

        for (int i = 0; i < board.Arrows.Count; i++)
        {
            foreach (Cell c in board.Arrows[i].Cells)
            {
                Cell[] neighbors =
                {
                    new(c.X + 1, c.Y),
                    new(c.X - 1, c.Y),
                    new(c.X, c.Y + 1),
                    new(c.X, c.Y - 1),
                };
                foreach (Cell n in neighbors)
                {
                    if (cellToIndex.TryGetValue(n, out int j) && j != i)
                    {
                        Assert.That(
                            colors[i],
                            Is.Not.EqualTo(colors[j]),
                            $"Arrows {i} and {j} adjacent at ({c.X},{c.Y})-({n.X},{n.Y}) share color {colors[i]}."
                        );
                    }
                }
            }
        }
    }
}
