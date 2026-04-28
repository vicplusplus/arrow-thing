/// <summary>
/// Result of attempting to clear an arrow. Blocked = 0 so all
/// success values are nonzero for easy truthiness checks.
/// </summary>
public enum ClearResult
{
    Blocked = 0,
    Cleared,
    ClearedFirst,
    ClearedLast,
}
