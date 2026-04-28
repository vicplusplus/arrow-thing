using System;
using System.Collections.Generic;

public sealed class Arrow
{
    private readonly List<Cell> _cells;

    public IReadOnlyList<Cell> Cells => _cells;
    public Cell HeadCell => _cells[0];
    public Direction HeadDirection { get; }

    /// <summary>Index assigned during board generation for bitset-based cycle detection. -1 when unset.</summary>
    internal int _generationIndex = -1;

    public Arrow(IEnumerable<Cell> cells)
    {
        _cells =
            cells == null ? throw new ArgumentNullException(nameof(cells)) : new List<Cell>(cells);

        if (_cells.Count < 2)
        {
            throw new ArgumentException(
                "Arrow requires at least 2 cells to derive head direction.",
                nameof(cells)
            );
        }

        HeadDirection = DeriveHeadDirection(_cells[0], _cells[1]);
    }

    public static (int dx, int dy) GetDirectionStep(Direction direction)
    {
        return direction switch
        {
            Direction.Up => (0, 1),
            Direction.Right => (1, 0),
            Direction.Down => (0, -1),
            Direction.Left => (-1, 0),
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unsupported arrow direction."
            ),
        };
    }

    private static Direction DeriveHeadDirection(Cell head, Cell next)
    {
        int dx = next.X - head.X;
        int dy = next.Y - head.Y;

        return (dx, dy) switch
        {
            (0, 1) => Direction.Down,
            (1, 0) => Direction.Left,
            (0, -1) => Direction.Up,
            (-1, 0) => Direction.Right,
            _ => throw new ArgumentException("Head and next cells must be orthogonally adjacent."),
        };
    }

    public enum Direction
    {
        Up,
        Right,
        Down,
        Left,
    }
}
