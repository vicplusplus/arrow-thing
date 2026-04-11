/// <summary>
/// Drains BoardGeneration.FillBoardIncremental synchronously for tests.
/// Mirrors the Unity-side helper in Assets/Tests/EditMode/TestBoardHelper.cs.
/// </summary>
public static class TestBoardHelper
{
    public static void FillBoard(Board board, int maxLength, int seed)
    {
        var enumerator = BoardGeneration.FillBoardIncremental(board, maxLength, seed);
        while (enumerator.MoveNext()) { }
    }
}
