using UnityEngine.SceneManagement;

/// <summary>
/// Scene navigation stack. Each transition fully unloads the previous scene
/// and loads the target fresh (single-mode). DontDestroyOnLoad singletons
/// survive all transitions.
///
/// Stack logic is delegated to <see cref="SceneNavStack"/> so it can be
/// unit-tested without loading real scenes.
/// </summary>
public static class SceneNav
{
    private static readonly SceneNavStack _stack = new SceneNavStack();

    /// <summary>
    /// Push the current scene onto the stack and load a new scene.
    /// The current scene is fully unloaded; Pop will reload it fresh.
    /// </summary>
    public static void Push(string sceneName)
    {
        string current = SceneManager.GetActiveScene().name;
        _stack.Push(current, sceneName);
        SceneManager.LoadScene(sceneName);
    }

    /// <summary>
    /// Load the previous scene from the stack (fresh).
    /// If the stack is empty, falls back to loading MainMenu.
    /// </summary>
    public static void Pop()
    {
        string previous = _stack.Pop();
        SceneManager.LoadScene(previous ?? "MainMenu");
    }

    /// <summary>
    /// Replace the current scene with a new one.
    /// The stack is unchanged — the new scene takes the current scene's position.
    /// </summary>
    public static void Replace(string sceneName)
    {
        string current = SceneManager.GetActiveScene().name;
        _stack.Replace(current, sceneName);
        SceneManager.LoadScene(sceneName);
    }

    /// <summary>
    /// Clear the stack and load a scene normally.
    /// Use for hard resets like returning to the title screen.
    /// </summary>
    public static void Reset(string sceneName)
    {
        _stack.Reset();
        SceneManager.LoadScene(sceneName);
    }

    /// <summary>Number of scenes on the stack (not counting the current active scene).</summary>
    public static int Depth => _stack.Depth;
}
