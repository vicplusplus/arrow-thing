/// <summary>
/// Test utilities for board generation. Drains <see cref="BoardGeneration.FillBoardIncremental"/>
/// synchronously so tests exercise the same code path as production.
/// </summary>
public static class TestBoardHelper
{
    public static void FillBoard(Board board, int maxLength, int seed)
    {
        var enumerator = BoardGeneration.FillBoardIncremental(board, maxLength, seed);
        while (enumerator.MoveNext()) { }
    }
}
