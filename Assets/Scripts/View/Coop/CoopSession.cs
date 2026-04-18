using System;
using UnityEngine;

/// <summary>
/// Client-side co-op session state for Phase 6+. Wraps a <see cref="CoopClient"/>
/// and a local <see cref="Board"/>, dispatches server events as typed C#
/// events, and provides a <see cref="TrySubmitClear"/> entry point for the
/// input handler to send clear attempts over the wire.
///
/// Optimistic model: the caller is expected to play the clear animation
/// immediately on <see cref="TrySubmitClear"/> (using the returned arrow
/// reference). The session later raises:
///   - <see cref="RemoteCleared"/> for accepted clears (remote or our own);
///   - <see cref="RemoteRejectedDep"/> for rejected dep clears (if it was
///     our tap, caller should rollback the optimistic animation);
///   - <see cref="LocalRejectedRace"/> when our tap hit an already-cleared
///     arrow (silent — animation should stay hidden);
///   - <see cref="LocalRejectedRate"/> when we're sending too fast;
///   - <see cref="LobbyCompleted"/> on the final clear.
/// </summary>
public sealed class CoopSession : IDisposable
{
    public Board Board { get; }
    public Guid YourUserId { get; private set; }
    public CoopClient Client { get; }
    public bool IsCompleted { get; private set; }

    /// <summary>Fired when the server accepts a clear (remote or ours).</summary>
    public event Action<ClearedEvent> RemoteCleared;

    /// <summary>Fired when the server broadcasts a rejected-dep clear.</summary>
    public event Action<RejectedDepEvent> RemoteRejectedDep;

    /// <summary>Fired when our own tap hit an already-cleared arrow (private reject).</summary>
    public event Action<RejectedRaceEvent> LocalRejectedRace;

    /// <summary>Fired when the server rate-limits our clear attempts.</summary>
    public event Action<long> LocalRejectedRate;

    /// <summary>Fired when the last arrow is cleared and the server broadcasts completion.</summary>
    public event Action LobbyCompleted;

    private long _nextClientSeq;
    private bool _disposed;

    public CoopSession(CoopClient client, Board board, Guid yourUserId)
    {
        Client = client;
        Board = board;
        YourUserId = yourUserId;

        Client.MessageReceived += OnMessageReceived;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Client.MessageReceived -= OnMessageReceived;
    }

    /// <summary>
    /// Submit a tap at <paramref name="cell"/> as a clear attempt to the
    /// server. Returns the local-state <see cref="Arrow"/> at that cell if
    /// present (so the caller can play the optimistic animation), or null
    /// if nothing is there locally. A non-null return means a
    /// <c>clear_attempt</c> was sent; null means nothing was sent.
    /// </summary>
    public Arrow TrySubmitClear(Cell cell, Vector3 tapWorld)
    {
        if (IsCompleted || _disposed)
            return null;

        var arrow = Board.Contains(cell) ? Board.GetArrowAt(cell) : null;
        if (arrow == null)
            return null;

        // Fire the wire message. Server will process atomically and respond
        // with either `cleared`, `rejected_dep`, `rejected_race`, or
        // `rejected_rate`.
        var clientSeq = ++_nextClientSeq;
        _ = Client.SendAsync(CoopMessage.ClearAttempt(tapWorld.x, tapWorld.y, clientSeq));

        return arrow;
    }

    // ── Message routing ──────────────────────────────────────────────────

    private void OnMessageReceived(CoopMessage msg)
    {
        switch (msg.Type)
        {
            case "cleared":
                HandleCleared(msg);
                break;
            case "rejected_dep":
                HandleRejectedDep(msg);
                break;
            case "rejected_race":
                HandleRejectedRace(msg);
                break;
            case "rejected_rate":
                HandleRejectedRate(msg);
                break;
            case "lobby_completed":
                IsCompleted = true;
                LobbyCompleted?.Invoke();
                break;
        }
    }

    private void HandleCleared(CoopMessage msg)
    {
        if (msg.Payload == null)
            return;
        var playerId = ParseGuid(msg.Payload.Value<string>("playerId"));
        var tapX = msg.Payload.Value<float>("tapX");
        var tapY = msg.Payload.Value<float>("tapY");
        var seq = msg.Payload.Value<long>("seq");

        var cell = new Cell(Mathf.RoundToInt(tapX), Mathf.RoundToInt(tapY));
        if (!Board.Contains(cell))
            return;

        var arrow = Board.GetArrowAt(cell);
        if (arrow == null)
            return; // already cleared locally via optimistic animation — dedup

        // Apply the clear to local board state.
        if (Board.IsClearable(arrow))
            Board.RemoveArrow(arrow);

        RemoteCleared?.Invoke(
            new ClearedEvent
            {
                PlayerId = playerId,
                Arrow = arrow,
                TapWorld = new Vector3(tapX, tapY, 0f),
                Seq = seq,
                IsLocal = playerId == YourUserId,
            }
        );
    }

    private void HandleRejectedDep(CoopMessage msg)
    {
        if (msg.Payload == null)
            return;
        var playerId = ParseGuid(msg.Payload.Value<string>("playerId"));
        var tapX = msg.Payload.Value<float>("tapX");
        var tapY = msg.Payload.Value<float>("tapY");
        var cell = new Cell(Mathf.RoundToInt(tapX), Mathf.RoundToInt(tapY));
        var arrow = Board.Contains(cell) ? Board.GetArrowAt(cell) : null;

        RemoteRejectedDep?.Invoke(
            new RejectedDepEvent
            {
                PlayerId = playerId,
                Arrow = arrow,
                TapWorld = new Vector3(tapX, tapY, 0f),
                IsLocal = playerId == YourUserId,
            }
        );
    }

    private void HandleRejectedRace(CoopMessage msg)
    {
        if (msg.Payload == null)
            return;
        var clientSeq = msg.Payload.Value<long>("clientSeq");
        var reason = msg.Payload.Value<string>("reason") ?? "race_lost";
        LocalRejectedRace?.Invoke(new RejectedRaceEvent { ClientSeq = clientSeq, Reason = reason });
    }

    private void HandleRejectedRate(CoopMessage msg)
    {
        if (msg.Payload == null)
            return;
        var clientSeq = msg.Payload.Value<long>("clientSeq");
        LocalRejectedRate?.Invoke(clientSeq);
    }

    private static Guid ParseGuid(string s)
    {
        return Guid.TryParse(s, out var g) ? g : Guid.Empty;
    }

    // ── Event payloads ───────────────────────────────────────────────────

    public struct ClearedEvent
    {
        public Guid PlayerId;
        public Arrow Arrow;
        public Vector3 TapWorld;
        public long Seq;
        public bool IsLocal;
    }

    public struct RejectedDepEvent
    {
        public Guid PlayerId;
        public Arrow Arrow;
        public Vector3 TapWorld;
        public bool IsLocal;
    }

    public struct RejectedRaceEvent
    {
        public long ClientSeq;
        public string Reason;
    }
}
