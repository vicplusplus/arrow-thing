using System;

namespace ArrowThing.Server.Coop;

/// <summary>
/// Fixed 12-color palette for co-op player colors. Deterministic assignment
/// by user id hash. Shared with the Unity client via the domain-layer mirror
/// at <c>Assets/Scripts/Domain/CoopColorPalette.cs</c> — keep the hex strings
/// and hash function in sync.
/// </summary>
public static class CoopColorPalette
{
    /// <summary>
    /// 12 colors chosen for legibility on both dark and light backgrounds.
    /// Uppercase hex with leading '#'.
    /// </summary>
    public static readonly string[] Colors =
    {
        "#E74C3C", // red
        "#3498DB", // blue
        "#2ECC71", // green
        "#F39C12", // orange
        "#9B59B6", // purple
        "#1ABC9C", // teal
        "#E91E63", // pink
        "#00BCD4", // cyan
        "#FF9800", // amber
        "#8BC34A", // lime
        "#FF5722", // deep orange
        "#607D8B", // blue-grey
    };

    /// <summary>
    /// Returns a deterministic default color for the given user id.
    /// Same user always maps to the same color across lobbies and sessions.
    /// </summary>
    public static string HexForGuid(Guid userId)
    {
        // Stable cross-platform hash: first 4 bytes of the GUID as a signed int.
        int hash = Math.Abs(BitConverter.ToInt32(userId.ToByteArray(), 0));
        return Colors[hash % Colors.Length];
    }
}
