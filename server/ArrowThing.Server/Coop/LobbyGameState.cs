using System;
using System.Threading;

namespace ArrowThing.Server.Coop;

/// <summary>
/// Per-lobby in-memory gameplay state for co-op sync (Phase 6).
///
/// Hydrated lazily by <see cref="CoopHub.GetOrHydrateGameStateAsync"/> on the
/// first WebSocket connection for an Active lobby. Contains the live
/// <see cref="Board"/> (reflects all accepted clears applied), the next event
/// sequence number, and a mutex that serializes clear-attempt processing so
/// concurrent taps can't both succeed on the same arrow.
///
/// Evicted 60 s after the last client disconnects (matching the spec's
/// reconnect grace window). Subsequent connects rehydrate from the DB snapshot
/// + <see cref="ArrowThing.Server.Models.LobbyEvent"/> replay.
/// </summary>
public class LobbyGameState
{
    public Board Board { get; }
    public int Seed { get; }
    public int MaxArrowLength { get; }

    /// <summary>Next event sequence number to assign. Monotonic, protected by <see cref="Mutex"/>.</summary>
    public long NextSeq;

    /// <summary>
    /// Serializes clear_attempt processing + snapshot re-encode for this lobby.
    /// Held for the duration of Board mutation + event insert + broadcast
    /// construction, so concurrent attempts see a consistent Board.
    /// </summary>
    public SemaphoreSlim Mutex { get; } = new(1, 1);

    /// <summary>
    /// When the last client disconnected. Used by the eviction scheduler.
    /// Updated by CoopHub on connect/disconnect events.
    /// </summary>
    public DateTime LastActiveAt { get; set; }

    /// <summary>
    /// Cancellation source for a pending eviction task. Set when the last
    /// client disconnects and a 60 s eviction timer starts. Cancelled when
    /// any client reconnects within the window.
    /// </summary>
    public CancellationTokenSource? EvictionCts { get; set; }

    public LobbyGameState(Board board, int seed, int maxArrowLength, long nextSeq)
    {
        Board = board;
        Seed = seed;
        MaxArrowLength = maxArrowLength;
        NextSeq = nextSeq;
        LastActiveAt = DateTime.UtcNow;
    }
}
