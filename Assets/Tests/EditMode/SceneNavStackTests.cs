using NUnit.Framework;

/// <summary>
/// Tests for <see cref="SceneNavStack"/> — the pure stack logic behind
/// <see cref="SceneNav"/>. Each test models a real user flow through
/// the game's scene graph and verifies the stack returns the correct
/// scene on Pop.
///
/// Note: Size Select is an in-scene state within MainMenu (managed by
/// MainMenuController.MenuState), not a separate scene. All menu sub-states
/// (Play, Singleplayer, SizeSelect, Multiplayer) are in-scene and do not
/// appear in the scene stack.
/// </summary>
[TestFixture]
public class SceneNavStackTests
{
    private SceneNavStack _stack;

    [SetUp]
    public void SetUp()
    {
        _stack = new SceneNavStack();
    }

    // ── Basic operations ────────────────────────────────────────────

    [Test]
    public void Push_IncreasesDepth()
    {
        _stack.Push("MainMenu", "Game");
        Assert.AreEqual(1, _stack.Depth);
    }

    [Test]
    public void Pop_EmptyStack_ReturnsNull()
    {
        Assert.IsNull(_stack.Pop());
    }

    [Test]
    public void Push_Pop_ReturnsPushedScene()
    {
        _stack.Push("MainMenu", "Game");
        Assert.AreEqual("MainMenu", _stack.Pop());
        Assert.AreEqual(0, _stack.Depth);
    }

    [Test]
    public void Replace_DoesNotChangeStack()
    {
        _stack.Push("MainMenu", "Game");
        Assert.AreEqual(1, _stack.Depth);
        _stack.Replace("Game", "Game");
        Assert.AreEqual(1, _stack.Depth);
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    [Test]
    public void Reset_ClearsStack()
    {
        _stack.Push("MainMenu", "Game");
        _stack.Push("Game", "Leaderboard");
        Assert.AreEqual(2, _stack.Depth);
        _stack.Reset();
        Assert.AreEqual(0, _stack.Depth);
    }

    [Test]
    public void ToArray_ReturnsBottomToTop()
    {
        _stack.Push("MainMenu", "Game");
        _stack.Push("Game", "Leaderboard");
        CollectionAssert.AreEqual(new[] { "MainMenu", "Game" }, _stack.ToArray());
    }

    // ── Real user flows ─────────────────────────────────────────────

    [Test]
    public void Flow_MainMenu_Leaderboard_Back()
    {
        // MainMenu → (Singleplayer, in-scene) → Leaderboard → Back
        _stack.Push("MainMenu", "Leaderboard");
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    [Test]
    public void Flow_Play_NewGame_Menu()
    {
        // MainMenu → (Play → Singleplayer → SizeSelect, all in-scene) → Game → Victory → Menu (Pop)
        _stack.Push("MainMenu", "Game");
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    [Test]
    public void Flow_Continue_Menu()
    {
        // MainMenu → (Singleplayer → Continue, in-scene) → Game → Victory → Menu (Pop)
        _stack.Push("MainMenu", "Game");
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    [Test]
    public void Flow_PlayAgain_ReplacesGame()
    {
        // MainMenu → Game → Victory → Play Again (Replace)
        _stack.Push("MainMenu", "Game");
        _stack.Replace("Game", "Game");
        // Stack unchanged — new Game is on top, MainMenu below.
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    // ── Victory → Leaderboard (the fixed bug) ──────────────────────

    [Test]
    public void Flow_Victory_Leaderboard_Back_ReturnsToMainMenu()
    {
        // MainMenu → Game → Victory → View Leaderboard (Replace) → Back
        _stack.Push("MainMenu", "Game");
        _stack.Replace("Game", "Leaderboard");
        // Back from Leaderboard returns to MainMenu.
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    [Test]
    public void Flow_Victory_Leaderboard_Replay_Back_Back()
    {
        // MainMenu → Game → Victory → Leaderboard (Replace) → Replay → Back → Back
        _stack.Push("MainMenu", "Game");
        _stack.Replace("Game", "Leaderboard");
        _stack.Push("Leaderboard", "Replay");
        Assert.AreEqual("Leaderboard", _stack.Pop());
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    // ── Regression: Push would have left Game on stack ──────────────

    [Test]
    public void Regression_Push_Instead_Of_Replace_Would_Return_To_Stale_Game()
    {
        // This test documents the bug that existed before the fix.
        // If Victory used Push instead of Replace, popping Leaderboard
        // would return to the dead Game scene.
        _stack.Push("MainMenu", "Game");
        // BUG: Push instead of Replace.
        _stack.Push("Game", "Leaderboard");
        // Pop returns to Game (stale!) instead of MainMenu.
        Assert.AreEqual("Game", _stack.Pop());
        // This is the WRONG result — the fix changes Push to Replace
        // so this path never happens in production.
    }

    // ── Leaderboard from MainMenu (not via Victory) ────────────────

    [Test]
    public void Flow_MainMenu_Leaderboard_Replay_Back_Back()
    {
        // MainMenu → Leaderboard → Replay → Back → Back
        _stack.Push("MainMenu", "Leaderboard");
        _stack.Push("Leaderboard", "Replay");
        Assert.AreEqual("Leaderboard", _stack.Pop());
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    // ── Quick Reset from gameplay ───────────────────────────────────

    [Test]
    public void Flow_QuickReset_Then_Menu()
    {
        // MainMenu → Game → Quick Reset (Replace) → Victory → Menu
        _stack.Push("MainMenu", "Game");
        _stack.Replace("Game", "Game");
        Assert.AreEqual("MainMenu", _stack.Pop());
    }

    [Test]
    public void Flow_MultipleQuickResets()
    {
        // MainMenu → Game → Reset → Reset → Reset → Menu
        _stack.Push("MainMenu", "Game");
        _stack.Replace("Game", "Game");
        _stack.Replace("Game", "Game");
        _stack.Replace("Game", "Game");
        Assert.AreEqual("MainMenu", _stack.Pop());
    }
}
