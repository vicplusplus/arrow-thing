using System;
using System.Collections.Generic;
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

    /// <summary>Fired after any <c>roster_full</c> or <c>roster_patch</c> is applied.</summary>
    public event Action RosterUpdated;

    /// <summary>Current roster state, keyed by user id. Mutated only on the main thread.</summary>
    public IReadOnlyDictionary<Guid, CoopPlayer> Roster => _roster;

    private readonly Dictionary<Guid, CoopPlayer> _roster = new();
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
    /// present (so the caller can play the appropriate animation), or null
    /// if nothing is there locally.
    ///
    /// Non-clearable taps are filtered locally and never cross the wire —
    /// they don't modify board state so other players don't need to see
    /// them, and this keeps autoclickers from generating server traffic.
    /// The server still validates on its side as defense-in-depth; a
    /// <c>clear_attempt</c> for a non-clearable arrow will receive a
    /// private <c>rejected_dep</c> reply.
    /// </summary>
    public Arrow TrySubmitClear(Cell cell, Vector3 tapWorld)
    {
        if (IsCompleted || _disposed)
            return null;

        var arrow = Board.Contains(cell) ? Board.GetArrowAt(cell) : null;
        if (arrow == null)
            return null;

        // Local guard: only clearable taps hit the wire. The caller still
        // gets the arrow reference back so it can play the blocked-tap
        // bump animation locally.
        if (!Board.IsClearable(arrow))
            return arrow;

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
            case "roster_full":
                HandleRosterFull(msg);
                break;
            case "roster_patch":
                HandleRosterPatch(msg);
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
        var newCount = msg.Payload.Value<int?>("newClearCount") ?? 0;
        var colorHex = msg.Payload.Value<string>("color") ?? "";
        var displayName = msg.Payload.Value<string>("displayName") ?? "";

        // Apply the authoritative clear-count + attribution to the roster
        // eagerly. The eventual roster_patch confirms it but we don't want
        // to wait 500 ms for the sidebar to tick.
        if (playerId != Guid.Empty)
        {
            if (_roster.TryGetValue(playerId, out var existing))
            {
                _roster[playerId] = existing.With(
                    clearCount: newCount,
                    color: string.IsNullOrEmpty(colorHex) ? existing.Color : ParseColor(colorHex),
                    displayName: string.IsNullOrEmpty(displayName)
                        ? existing.DisplayName
                        : displayName
                );
            }
            else if (!string.IsNullOrEmpty(displayName))
            {
                _roster[playerId] = new CoopPlayer(
                    playerId,
                    displayName,
                    ParseColor(colorHex),
                    newCount,
                    0,
                    online: true,
                    isLocal: playerId == YourUserId
                );
            }
            RosterUpdated?.Invoke();
        }

        var cell = new Cell(Mathf.RoundToInt(tapX), Mathf.RoundToInt(tapY));
        if (!Board.Contains(cell))
            return;

        var arrow = Board.GetArrowAt(cell);
        if (arrow == null)
        {
            // Local board already removed the arrow (our optimistic path, or
            // a prior cleared broadcast we already applied). Still raise the
            // event so callers can spawn the remote tap indicator if this
            // wasn't our own tap.
            RemoteCleared?.Invoke(
                new ClearedEvent
                {
                    PlayerId = playerId,
                    Arrow = null,
                    TapWorld = new Vector3(tapX, tapY, 0f),
                    Seq = seq,
                    IsLocal = playerId == YourUserId,
                    Color = ParseColor(colorHex),
                    NewClearCount = newCount,
                    DisplayName = displayName,
                }
            );
            return;
        }

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
                Color = ParseColor(colorHex),
                NewClearCount = newCount,
                DisplayName = displayName,
            }
        );
    }

    private void HandleRosterFull(CoopMessage msg)
    {
        if (msg.Payload == null)
            return;
        _roster.Clear();
        var players = msg.Payload["players"];
        if (players != null)
        {
            foreach (var p in players)
                ApplyRosterEntry(p);
        }
        RosterUpdated?.Invoke();
    }

    private void HandleRosterPatch(CoopMessage msg)
    {
        if (msg.Payload == null)
            return;
        var upsert = msg.Payload["upsert"];
        if (upsert != null)
        {
            foreach (var p in upsert)
                ApplyRosterEntry(p);
        }
        var remove = msg.Payload["remove"];
        if (remove != null)
        {
            foreach (var id in remove)
            {
                var guid = ParseGuid(id.Value<string>());
                if (guid != Guid.Empty)
                    _roster.Remove(guid);
            }
        }
        RosterUpdated?.Invoke();
    }

    private void ApplyRosterEntry(Newtonsoft.Json.Linq.JToken entry)
    {
        var id = ParseGuid(entry.Value<string>("userId"));
        if (id == Guid.Empty)
            return;
        var displayName = entry.Value<string>("displayName") ?? "";
        var color = ParseColor(entry.Value<string>("color") ?? "#FFFFFF");
        var clearCount = entry.Value<int?>("clearCount") ?? 0;
        var accumulatedMillis = entry.Value<long?>("accumulatedMillis") ?? 0;
        var online = entry.Value<bool?>("online") ?? false;
        _roster[id] = new CoopPlayer(
            id,
            displayName,
            color,
            clearCount,
            accumulatedMillis,
            online,
            isLocal: id == YourUserId
        );
    }

    private static Color32 ParseColor(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return new Color32(255, 255, 255, 255);
        var s = hex.StartsWith("#") ? hex.Substring(1) : hex;
        if (s.Length != 6)
            return new Color32(255, 255, 255, 255);
        try
        {
            return new Color32(
                Convert.ToByte(s.Substring(0, 2), 16),
                Convert.ToByte(s.Substring(2, 2), 16),
                Convert.ToByte(s.Substring(4, 2), 16),
                255
            );
        }
        catch
        {
            return new Color32(255, 255, 255, 255);
        }
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

        /// <summary>
        /// Arrow at the tapped cell, if we could still find it locally. Null
        /// when our optimistic path already removed it or when we raced with
        /// a prior broadcast — in that case, the caller should not re-play
        /// the clear animation.
        /// </summary>
        public Arrow Arrow;
        public Vector3 TapWorld;
        public long Seq;
        public bool IsLocal;
        public Color32 Color;
        public int NewClearCount;
        public string DisplayName;
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
