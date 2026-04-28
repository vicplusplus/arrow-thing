using System.Collections.Generic;

/// <summary>
/// Pure stack logic for scene navigation. Unity-independent so it can be
/// unit-tested without loading real scenes.
///
/// Each operation models the stack side-effect of the corresponding
/// <c>SceneNav</c> operation and returns the information the caller needs
/// to perform the actual scene load/unload.
/// </summary>
public sealed class SceneNavStack
{
    private readonly Stack<string> _stack = new Stack<string>();

    /// <summary>Number of scenes on the stack (not counting the active scene).</summary>
    public int Depth => _stack.Count;

    /// <summary>
    /// Push the current scene onto the stack so it can be restored later.
    /// Returns nothing — the caller loads <paramref name="target"/> additively.
    /// </summary>
    public void Push(string current, string target)
    {
        _stack.Push(current);
    }

    /// <summary>
    /// Pop the previous scene from the stack.
    /// Returns the scene name to re-enable, or null if the stack is empty
    /// (caller should fall back to loading a default scene).
    /// </summary>
    public string Pop()
    {
        return _stack.Count > 0 ? _stack.Pop() : null;
    }

    /// <summary>
    /// Replace has no stack side-effect — the new scene takes the current
    /// scene's position without touching the stack.
    /// </summary>
    public void Replace(string current, string target)
    {
        // No-op on the stack. Current is unloaded and target is loaded,
        // but the stack remains unchanged.
    }

    /// <summary>
    /// Clear the entire stack (hard reset).
    /// </summary>
    public void Reset()
    {
        _stack.Clear();
    }

    /// <summary>
    /// Returns a snapshot of the stack for test assertions (bottom to top).
    /// </summary>
    public string[] ToArray()
    {
        var arr = _stack.ToArray();
        // Stack<T>.ToArray() returns top-to-bottom; reverse for readability.
        System.Array.Reverse(arr);
        return arr;
    }
}
