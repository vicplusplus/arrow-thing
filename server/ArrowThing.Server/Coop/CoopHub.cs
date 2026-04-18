using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ArrowThing.Server.Auth;
using ArrowThing.Server.Data;
using ArrowThing.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ArrowThing.Server.Coop;

/// <summary>
/// WebSocket hub for co-op lobbies. Singleton — owns the per-lobby in-memory
/// session state. Dispatches messages by type and broadcasts progress /
/// completion events from the generation worker via Redis pub/sub.
/// </summary>
public class CoopHub
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GenerationProgressBus _progressBus;
    private readonly ILogger<CoopHub> _logger;
    private readonly ConcurrentDictionary<string, LobbyRoom> _rooms = new();

    /// <summary>
    /// Per-lobby live gameplay state (Board + event Seq counter + mutex).
    /// Hydrated lazily on the first clear_attempt or hello for an Active lobby.
    /// Evicted 60 s after the last client disconnects.
    /// </summary>
    private readonly ConcurrentDictionary<string, LobbyGameState> _gameStates = new();

    /// <summary>Hold state in memory for this long after the last disconnect.</summary>
    private static readonly TimeSpan EvictionGrace = TimeSpan.FromSeconds(60);

    private bool _busSubscribed;

    // Upper bound on a single inbound message. Coop traffic is small game-state
    // deltas; anything larger is abusive and must not be buffered in memory.
    internal const int MaxMessageBytes = 256 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CoopHub(
        IServiceScopeFactory scopeFactory,
        GenerationProgressBus progressBus,
        ILogger<CoopHub> logger
    )
    {
        _scopeFactory = scopeFactory;
        _progressBus = progressBus;
        _logger = logger;
    }

    /// <summary>
    /// Subscribes to the generation progress bus. Call once at startup.
    /// Safe to call multiple times — only the first call subscribes.
    /// </summary>
    public async Task EnsureSubscribedAsync()
    {
        if (_busSubscribed)
            return;
        _busSubscribed = true;
        await _progressBus.SubscribeAsync(OnBusMessageAsync);
    }

    private async Task OnBusMessageAsync(string lobbyCode, GenerationProgressBus.BusMessage msg)
    {
        var code = NormalizeCode(lobbyCode);
        if (!_rooms.TryGetValue(code, out var room))
            return;

        CoopMessage? envelope = msg.Type switch
        {
            "gen_progress" => new CoopMessage
            {
                Type = "gen_progress",
                Payload = ToJsonElement(new GenProgressPayload(msg.Pct ?? 0)),
            },
            "gen_complete" => new CoopMessage { Type = "gen_complete" },
            "lobby_failed" => new CoopMessage
            {
                Type = "lobby_failed",
                Payload = ToJsonElement(new DisconnectPayload(msg.Reason ?? "unknown")),
            },
            _ => null,
        };
        if (envelope == null)
            return;

        await BroadcastAsync(code, envelope);

        // On gen_complete, push the snapshot to all connected clients so they
        // don't have to manually re-send hello.
        if (msg.Type == "gen_complete")
        {
            await SendSnapshotToRoomAsync(room);
        }
    }

    private async Task SendSnapshotToRoomAsync(LobbyRoom room)
    {
        Lobby? lobby;
        byte[]? snapshotBytes;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            lobby = await db.Lobbies.FirstOrDefaultAsync(l => l.Code == room.Code);
            if (lobby == null)
                return;
            var snapRepo = scope.ServiceProvider.GetRequiredService<LobbySnapshotRepository>();
            var snap = await snapRepo.LoadAsync(lobby.Id);
            snapshotBytes = snap?.Data;
        }

        if (snapshotBytes == null)
            return;

        var meta = new CoopMessage
        {
            Type = "snapshot",
            Payload = ToJsonElement(new SnapshotMetaPayload(1, snapshotBytes.Length)),
        };

        foreach (var kvp in room.Connections)
        {
            var entry = kvp.Value;
            if (!entry.Ready)
                continue;
            try
            {
                await entry.WriteLock.WaitAsync();
                try
                {
                    await SendRawAsync(entry.Socket, meta, CancellationToken.None);
                    await SendBinaryRawAsync(entry.Socket, snapshotBytes, CancellationToken.None);
                }
                finally
                {
                    entry.WriteLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[CoopHub] Failed to send snapshot to user {UserId}",
                    kvp.Key
                );
            }
        }
    }

    private async Task BroadcastAsync(string normalizedCode, CoopMessage msg)
    {
        if (!_rooms.TryGetValue(normalizedCode, out var room))
            return;
        foreach (var kvp in room.Connections)
        {
            var entry = kvp.Value;
            if (!entry.Ready)
                continue;
            try
            {
                await SendAsync(entry, msg, CancellationToken.None);
            }
            catch
            {
                // Failed sockets get cleaned up by their own receive loops.
            }
        }
    }

    private static async Task SendBinaryRawAsync(
        WebSocket socket,
        byte[] data,
        CancellationToken ct
    )
    {
        if (socket.State != WebSocketState.Open)
            return;
        await socket.SendAsync(
            new ArraySegment<byte>(data),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            ct
        );
    }

    private static async Task SendBinaryAsync(
        ConnectionEntry entry,
        byte[] data,
        CancellationToken ct
    )
    {
        await entry.WriteLock.WaitAsync(ct);
        try
        {
            await SendBinaryRawAsync(entry.Socket, data, ct);
        }
        finally
        {
            entry.WriteLock.Release();
        }
    }

    /// <summary>Total active rooms (for diagnostics/tests).</summary>
    public int RoomCount => _rooms.Count;

    /// <summary>Connected user count for a lobby (for diagnostics/tests). 0 if no room.</summary>
    public int ConnectedCount(string code) =>
        _rooms.TryGetValue(NormalizeCode(code), out var room) ? room.Connections.Count : 0;

    /// <summary>
    /// Handle a single incoming WebSocket connection lifecycle. Caller must
    /// have already accepted the WebSocket via <c>HttpContext.WebSockets.AcceptWebSocketAsync</c>.
    /// </summary>
    public async Task HandleConnectionAsync(
        WebSocket socket,
        Lobby lobby,
        Guid userId,
        CancellationToken ct
    )
    {
        var code = NormalizeCode(lobby.Code);
        var room = _rooms.GetOrAdd(code, _ => new LobbyRoom(code));
        var entry = new ConnectionEntry(socket);
        room.Connections[userId] = entry;

        // Cancel any pending eviction — a client reconnected within the grace window.
        if (_gameStates.TryGetValue(code, out var reuseState))
        {
            reuseState.EvictionCts?.Cancel();
            reuseState.EvictionCts = null;
        }

        _logger.LogInformation(
            "[CoopHub] User {UserId} connected to lobby {Code} (now {Count} connected)",
            userId,
            code,
            room.Connections.Count
        );

        try
        {
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult result;
                var overflow = false;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    if (ms.Length + result.Count > MaxMessageBytes)
                    {
                        overflow = true;
                        break;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (overflow)
                {
                    _logger.LogWarning(
                        "[CoopHub] Closing socket for user {UserId} on lobby {Code}: message exceeded {Max} bytes",
                        userId,
                        code,
                        MaxMessageBytes
                    );
                    await socket.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig,
                        "message too large",
                        CancellationToken.None
                    );
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "client closed",
                        CancellationToken.None
                    );
                    break;
                }

                if (ms.Length == 0)
                    continue;

                CoopMessage? msg;
                try
                {
                    msg = JsonSerializer.Deserialize<CoopMessage>(ms.ToArray(), JsonOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[CoopHub] Bad JSON from {UserId}", userId);
                    continue;
                }
                if (msg == null || string.IsNullOrEmpty(msg.Type))
                    continue;

                await HandleMessageAsync(msg, entry, lobby, userId, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogInformation(
                "[CoopHub] WebSocket error for user {UserId} on lobby {Code}: {Message}",
                userId,
                code,
                ex.Message
            );
        }
        finally
        {
            room.Connections.TryRemove(userId, out _);
            if (room.Connections.IsEmpty)
            {
                _rooms.TryRemove(code, out _);
                ScheduleGameStateEviction(code);
            }

            _logger.LogInformation(
                "[CoopHub] User {UserId} disconnected from lobby {Code}",
                userId,
                code
            );
        }
    }

    /// <summary>
    /// Schedule the in-memory game state to be evicted after the grace window.
    /// If a client reconnects within the window, HandleConnectionAsync cancels
    /// the token and the state survives.
    /// </summary>
    private void ScheduleGameStateEviction(string normalizedCode)
    {
        if (!_gameStates.TryGetValue(normalizedCode, out var state))
            return;

        state.LastActiveAt = DateTime.UtcNow;
        var cts = new CancellationTokenSource();
        state.EvictionCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(EvictionGrace, cts.Token);
                if (_gameStates.TryRemove(normalizedCode, out _))
                {
                    _logger.LogInformation(
                        "[CoopHub] Evicted game state for lobby {Code} after {Grace}s idle",
                        normalizedCode,
                        EvictionGrace.TotalSeconds
                    );
                }
            }
            catch (OperationCanceledException)
            {
                // Client reconnected; state survives.
            }
        });
    }

    private async Task HandleMessageAsync(
        CoopMessage msg,
        ConnectionEntry entry,
        Lobby lobby,
        Guid userId,
        CancellationToken ct
    )
    {
        switch (msg.Type)
        {
            case "hello":
                await HandleHelloAsync(entry, lobby, userId, ct);
                break;

            case "clear_attempt":
                await HandleClearAttemptAsync(msg, entry, lobby, userId, ct);
                break;

            case "heartbeat":
                HandleHeartbeat(msg, entry);
                break;

            case "echo":
                // Debug round-trip — returns the same payload tagged echo_reply.
                await SendAsync(
                    entry,
                    new CoopMessage
                    {
                        Type = "echo_reply",
                        Seq = msg.Seq,
                        Payload = msg.Payload,
                    },
                    ct
                );
                break;

            case "goodbye":
                await entry.Socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "goodbye",
                    CancellationToken.None
                );
                break;

            default:
                _logger.LogWarning(
                    "[CoopHub] Unknown message type {Type} from user {UserId}",
                    msg.Type,
                    userId
                );
                break;
        }
    }

    // ── Phase 6: gameplay sync ───────────────────────────────────────────────

    /// <summary>
    /// Returns the in-memory game state for a lobby, hydrating from DB on miss.
    /// Thread-safe: multiple callers will only hydrate once via double-checked
    /// locking. Lobby must be Active — Generating/Failed/Completed/Deleted
    /// return null.
    /// </summary>
    private async Task<LobbyGameState?> GetOrHydrateGameStateAsync(string normalizedCode)
    {
        if (_gameStates.TryGetValue(normalizedCode, out var existing))
            return existing;

        // Hydrate. Serialize across callers for the same lobby so we only do
        // this work once. GetOrAdd with factory isn't async-safe, so we use
        // the usual two-phase: try, build, TryAdd, check again.
        Lobby? lobby;
        byte[]? snapshotBytes;
        long nextSeq;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            lobby = await db.Lobbies.FirstOrDefaultAsync(l => l.Code == normalizedCode);
            if (lobby == null || lobby.Status != LobbyStatus.Active)
                return null;

            var snapRepo = scope.ServiceProvider.GetRequiredService<LobbySnapshotRepository>();
            var snap = await snapRepo.LoadAsync(lobby.Id);
            snapshotBytes = snap?.Data;
            if (snapshotBytes == null)
                return null;

            // Next Seq is one past the max existing event for this lobby.
            var maxSeq = await db
                .LobbyEvents.Where(e => e.LobbyId == lobby.Id)
                .Select(e => (long?)e.Seq)
                .MaxAsync();
            nextSeq = (maxSeq ?? -1) + 1;
        }

        var board = BinarySnapshot.DecodeFull(snapshotBytes);

        // Replay accepted clears so the in-memory board matches persisted state.
        // DB query re-opened because we're outside the original scope; also
        // outside the lobby-wide lock, which is fine because replay is
        // idempotent and we'll TryAdd below.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var events = await db
                .LobbyEvents.Where(e =>
                    e.LobbyId == lobby.Id && e.Type == LobbyEventType.ClearAccepted
                )
                .OrderBy(e => e.Seq)
                .ToListAsync();

            foreach (var ev in events)
            {
                if (!ev.TapX.HasValue || !ev.TapY.HasValue)
                    continue;
                var cell = TapToCell(ev.TapX.Value, ev.TapY.Value);
                if (!board.Contains(cell))
                    continue;
                var arrow = board.GetArrowAt(cell);
                if (arrow != null && board.IsClearable(arrow))
                    board.RemoveArrow(arrow);
            }
        }

        var state = new LobbyGameState(board, (int)lobby.Seed, lobby.MaxArrowLength, nextSeq);
        if (_gameStates.TryAdd(normalizedCode, state))
        {
            _logger.LogInformation(
                "[CoopHub] Hydrated game state for lobby {Code}: {ArrowCount} arrows, nextSeq={Seq}",
                normalizedCode,
                board.Arrows.Count,
                nextSeq
            );
            return state;
        }

        // Lost the race — another hydrate call won. Use theirs.
        return _gameStates.TryGetValue(normalizedCode, out var winner) ? winner : state;
    }

    /// <summary>
    /// Round-to-nearest cell conversion, matching the client-side
    /// <c>BoardCoords.WorldToCell</c>. World units are 1 per cell, centered
    /// on integer coordinates.
    /// </summary>
    private static Cell TapToCell(float tapX, float tapY)
    {
        int x = (int)Math.Round(tapX);
        int y = (int)Math.Round(tapY);
        return new Cell(x, y);
    }

    private async Task HandleClearAttemptAsync(
        CoopMessage msg,
        ConnectionEntry entry,
        Lobby lobby,
        Guid userId,
        CancellationToken ct
    )
    {
        // Rate limit check (token bucket, per connection).
        var now = DateTime.UtcNow;
        lock (entry)
        {
            var elapsed = (now - entry.LastRefillAt).TotalSeconds;
            entry.Tokens = Math.Min(
                ConnectionEntry.ClearRateCapacity,
                entry.Tokens + elapsed * ConnectionEntry.ClearRateRefillPerSecond
            );
            entry.LastRefillAt = now;

            if (entry.Tokens < 1.0)
            {
                // Over-rate: drop silently, track sustained duration.
                entry.OverRateSince ??= now;
                if (
                    (now - entry.OverRateSince.Value).TotalSeconds
                    >= ConnectionEntry.ClearRateSustainedDisconnectSeconds
                )
                {
                    // Fall through: send rejected_rate + disconnect below (outside lock).
                }
                else
                {
                    // Silent drop.
                    return;
                }
            }
            else
            {
                entry.Tokens -= 1.0;
                entry.OverRateSince = null;
            }
        }

        // If we're in sustained over-rate, disconnect.
        if (
            entry.OverRateSince.HasValue
            && (DateTime.UtcNow - entry.OverRateSince.Value).TotalSeconds
                >= ConnectionEntry.ClearRateSustainedDisconnectSeconds
        )
        {
            var clientSeq =
                msg.Payload?.Deserialize<ClearAttemptPayload>(JsonOptions)?.ClientSeq ?? 0;
            await SendAsync(
                entry,
                new CoopMessage
                {
                    Type = "rejected_rate",
                    Payload = ToJsonElement(new RejectedRatePayload(clientSeq, 5000)),
                },
                ct
            );
            await SendAsync(
                entry,
                new CoopMessage
                {
                    Type = "disconnect",
                    Payload = ToJsonElement(new DisconnectPayload("rate_limited")),
                },
                ct
            );
            await entry.Socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "rate_limited",
                CancellationToken.None
            );
            return;
        }

        // Parse payload.
        ClearAttemptPayload? payload;
        try
        {
            payload = msg.Payload?.Deserialize<ClearAttemptPayload>(JsonOptions);
        }
        catch
        {
            return; // malformed
        }
        if (payload == null)
            return;

        var code = NormalizeCode(lobby.Code);
        var state = await GetOrHydrateGameStateAsync(code);
        if (state == null)
        {
            // Lobby not active — silently ignore. Status should already reflect this.
            return;
        }

        entry.LastInputAt = DateTime.UtcNow;

        var cell = TapToCell(payload.TapX, payload.TapY);

        await state.Mutex.WaitAsync(ct);
        try
        {
            if (!state.Board.Contains(cell))
            {
                // Tap landed outside the board (user clicked on empty space).
                // Treat as race — private reject to attempter only.
                await SendAsync(
                    entry,
                    new CoopMessage
                    {
                        Type = "rejected_race",
                        Payload = ToJsonElement(
                            new RejectedRacePayload(payload.ClientSeq, "out_of_bounds")
                        ),
                    },
                    ct
                );
                return;
            }

            var arrow = state.Board.GetArrowAt(cell);
            if (arrow == null)
            {
                // Already cleared by someone else. Private reject.
                await SendAsync(
                    entry,
                    new CoopMessage
                    {
                        Type = "rejected_race",
                        Payload = ToJsonElement(
                            new RejectedRacePayload(payload.ClientSeq, "race_lost")
                        ),
                    },
                    ct
                );
                return;
            }

            if (!state.Board.IsClearable(arrow))
            {
                // Dependency not satisfied. Non-clearable taps are filtered
                // client-side; reaching the server means either a client bug
                // or a cheater bypassing the local guard. Reply privately to
                // the sender (so a legit client can correct its local state)
                // and don't persist or broadcast — non-clearable taps don't
                // modify board state and shouldn't pollute the event log.
                await SendAsync(
                    entry,
                    new CoopMessage
                    {
                        Type = "rejected_dep",
                        Payload = ToJsonElement(
                            new RejectedDepPayload(
                                userId,
                                payload.TapX,
                                payload.TapY,
                                DateTime.UtcNow.ToString("O"),
                                0
                            )
                        ),
                    },
                    ct
                );
                return;
            }

            // Accepted: mutate board, persist event, broadcast.
            state.Board.RemoveArrow(arrow);
            var acceptSeq = state.NextSeq++;
            await PersistEventAsync(
                lobby.Id,
                acceptSeq,
                LobbyEventType.ClearAccepted,
                userId,
                payload.TapX,
                payload.TapY
            );

            await BroadcastAsync(
                code,
                new CoopMessage
                {
                    Type = "cleared",
                    Seq = acceptSeq,
                    Payload = ToJsonElement(
                        new ClearedPayload(
                            userId,
                            payload.TapX,
                            payload.TapY,
                            DateTime.UtcNow.ToString("O"),
                            acceptSeq
                        )
                    ),
                }
            );

            // Completion: last arrow cleared → transition to Completed + broadcast.
            if (state.Board.Arrows.Count == 0)
            {
                var completeSeq = state.NextSeq++;
                var completedAt = DateTime.UtcNow;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var lobbyRow = await db.Lobbies.FindAsync(lobby.Id);
                    if (lobbyRow != null && lobbyRow.Status == LobbyStatus.Active)
                    {
                        lobbyRow.Status = LobbyStatus.Completed;
                        lobbyRow.CompletedAt = completedAt;
                        lobbyRow.LastActivityAt = completedAt;
                        db.LobbyEvents.Add(
                            new LobbyEvent
                            {
                                LobbyId = lobby.Id,
                                Seq = completeSeq,
                                Type = LobbyEventType.LobbyCompleted,
                                CreatedAt = completedAt,
                            }
                        );
                        await db.SaveChangesAsync();
                    }
                }
                await BroadcastAsync(
                    code,
                    new CoopMessage
                    {
                        Type = "lobby_completed",
                        Seq = completeSeq,
                        Payload = ToJsonElement(
                            new LobbyCompletedPayload(completedAt.ToString("O"))
                        ),
                    }
                );
            }
        }
        finally
        {
            state.Mutex.Release();
        }
    }

    private async Task PersistEventAsync(
        Guid lobbyId,
        long seq,
        LobbyEventType type,
        Guid userId,
        float tapX,
        float tapY
    )
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LobbyEvents.Add(
            new LobbyEvent
            {
                LobbyId = lobbyId,
                Seq = seq,
                Type = type,
                UserId = userId,
                TapX = tapX,
                TapY = tapY,
                CreatedAt = DateTime.UtcNow,
            }
        );
        await db.SaveChangesAsync();
    }

    private void HandleHeartbeat(CoopMessage msg, ConnectionEntry entry)
    {
        HeartbeatPayload? payload;
        try
        {
            payload = msg.Payload?.Deserialize<HeartbeatPayload>(JsonOptions);
        }
        catch
        {
            return;
        }
        if (payload == null)
            return;

        entry.LastHeartbeatAt = DateTime.UtcNow;
        entry.Focused = payload.Focused;
        if (
            DateTime.TryParse(
                payload.LastInputTsUtc,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var lastInput
            )
        )
        {
            entry.LastInputAt = lastInput;
        }
    }

    // ── Existing methods ─────────────────────────────────────────────────────

    private async Task HandleHelloAsync(
        ConnectionEntry entry,
        Lobby lobbyAtConnect,
        Guid userId,
        CancellationToken ct
    )
    {
        // Refetch current lobby state — it may have transitioned (Generating
        // → Active, etc.) since the connection was accepted.
        Lobby? current;
        byte[]? snapshotBytes = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            current = await db.Lobbies.FindAsync(lobbyAtConnect.Id);
            if (current == null)
            {
                await SendAsync(
                    entry,
                    new CoopMessage
                    {
                        Type = "disconnect",
                        Payload = ToJsonElement(new DisconnectPayload("lobby_not_found")),
                    },
                    ct
                );
                await entry.Socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "lobby_not_found",
                    CancellationToken.None
                );
                return;
            }

            // Auto-register the player if not already registered.
            var existingReg = await db.LobbyRegistrations.FindAsync(lobbyAtConnect.Id, userId);
            if (existingReg == null)
            {
                // Enforce 50-per-user active registration cap.
                var lobbyService = scope.ServiceProvider.GetRequiredService<LobbyService>();
                var activeCount = await lobbyService.CountActiveRegistrationsAsync(userId);
                if (activeCount >= LobbyService.MaxRegistrationsPerUser)
                {
                    await SendAsync(
                        entry,
                        new CoopMessage
                        {
                            Type = "disconnect",
                            Payload = ToJsonElement(new DisconnectPayload("registration_cap")),
                        },
                        ct
                    );
                    await entry.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "registration_cap",
                        CancellationToken.None
                    );
                    return;
                }

                var user = await db.Users.FindAsync(userId);
                var color = user?.CoopColor ?? CoopColorPalette.HexForGuid(userId);
                var now = DateTime.UtcNow;
                db.LobbyRegistrations.Add(
                    new LobbyRegistration
                    {
                        LobbyId = lobbyAtConnect.Id,
                        UserId = userId,
                        DisplayNameAtJoin = user?.DisplayName ?? "",
                        ColorAtJoin = color,
                        JoinedAt = now,
                        LastActivityAt = now,
                    }
                );
                await db.SaveChangesAsync();
            }
            else
            {
                // Existing registration — just bump activity timestamp.
                existingReg.LastActivityAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            // Snapshot loading is handled below (after the scope) so we can
            // re-encode from live in-memory state if hydrated.
        }

        // Prefer re-encoding from live in-memory game state — it reflects all
        // clears applied since the lobby went Active. Falls back to the raw
        // DB snapshot when the state hasn't been hydrated yet (e.g., first
        // connect for a long-idle Active lobby).
        if (current.Status == LobbyStatus.Active)
        {
            var state = await GetOrHydrateGameStateAsync(NormalizeCode(current.Code));
            if (state != null)
            {
                await state.Mutex.WaitAsync(ct);
                try
                {
                    snapshotBytes = BinarySnapshot.EncodeFull(
                        state.Board,
                        state.Seed,
                        state.MaxArrowLength,
                        gzip: true
                    );
                }
                finally
                {
                    state.Mutex.Release();
                }
            }
            else
            {
                using var snapScope = _scopeFactory.CreateScope();
                var snapRepo =
                    snapScope.ServiceProvider.GetRequiredService<LobbySnapshotRepository>();
                var snap = await snapRepo.LoadAsync(current.Id);
                snapshotBytes = snap?.Data;
            }
        }

        // Hold the per-connection write lock for the whole handshake so a
        // concurrent bus broadcast can't interleave between welcome and the
        // snapshot frames. While Ready == false, BroadcastAsync skips this
        // connection entirely — the lock only becomes contended after Ready
        // flips below.
        await entry.WriteLock.WaitAsync(ct);
        try
        {
            await SendRawAsync(
                entry.Socket,
                new CoopMessage
                {
                    Type = "welcome",
                    Seq = 0,
                    Payload = ToJsonElement(
                        new WelcomePayload(
                            userId,
                            current.Code,
                            current.Name,
                            (short)current.Status
                        )
                    ),
                },
                ct
            );

            switch (current.Status)
            {
                case LobbyStatus.Generating:
                    // Subscriber will deliver progress events via the bus.
                    break;

                case LobbyStatus.Active:
                    if (snapshotBytes != null)
                    {
                        await SendRawAsync(
                            entry.Socket,
                            new CoopMessage
                            {
                                Type = "snapshot",
                                Payload = ToJsonElement(
                                    new SnapshotMetaPayload(1, snapshotBytes.Length)
                                ),
                            },
                            ct
                        );
                        await SendBinaryRawAsync(entry.Socket, snapshotBytes, ct);
                    }
                    break;

                case LobbyStatus.GenerationFailed:
                    await SendRawAsync(
                        entry.Socket,
                        new CoopMessage
                        {
                            Type = "lobby_failed",
                            Payload = ToJsonElement(new DisconnectPayload("generation_failed")),
                        },
                        ct
                    );
                    break;

                case LobbyStatus.Completed:
                    await SendRawAsync(
                        entry.Socket,
                        new CoopMessage
                        {
                            Type = "disconnect",
                            Payload = ToJsonElement(new DisconnectPayload("lobby_completed")),
                        },
                        ct
                    );
                    await entry.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "completed",
                        CancellationToken.None
                    );
                    break;

                case LobbyStatus.Deleted:
                    await SendRawAsync(
                        entry.Socket,
                        new CoopMessage
                        {
                            Type = "disconnect",
                            Payload = ToJsonElement(new DisconnectPayload("lobby_deleted")),
                        },
                        ct
                    );
                    await entry.Socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "deleted",
                        CancellationToken.None
                    );
                    break;
            }
        }
        finally
        {
            entry.WriteLock.Release();
        }

        entry.Ready = true;
    }

    private static async Task SendRawAsync(WebSocket socket, CoopMessage msg, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open)
            return;
        var json = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        await socket.SendAsync(
            new ArraySegment<byte>(json),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct
        );
    }

    private static async Task SendAsync(
        ConnectionEntry entry,
        CoopMessage msg,
        CancellationToken ct
    )
    {
        await entry.WriteLock.WaitAsync(ct);
        try
        {
            await SendRawAsync(entry.Socket, msg, ct);
        }
        finally
        {
            entry.WriteLock.Release();
        }
    }

    private static JsonElement ToJsonElement<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static string NormalizeCode(string code) => (code ?? "").Trim().ToUpperInvariant();

    // -- JWT validation for WebSocket query string --------------------------

    /// <summary>
    /// Validates a JWT from the WebSocket query string. Returns the user ID on
    /// success, or null on failure. Also verifies the security stamp matches
    /// the database (matches the existing API auth middleware).
    /// </summary>
    public static async Task<Guid?> ValidateTokenAsync(
        string? token,
        JwtHelper jwt,
        AppDbContext db,
        ILogger logger
    )
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var handler = new JwtSecurityTokenHandler();
        try
        {
            var principal = handler.ValidateToken(token, jwt.GetValidationParameters(), out _);
            // JwtBearer maps `sub` to ClaimTypes.NameIdentifier on validation; check both.
            var sub =
                principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = principal.FindFirstValue("security_stamp");
            if (sub == null || stamp == null)
                return null;

            if (!Guid.TryParse(sub, out var userId))
                return null;

            var user = await db.Users.FindAsync(userId);
            if (user == null || user.SecurityStamp != stamp)
                return null;

            return userId;
        }
        catch (SecurityTokenException ex)
        {
            logger.LogInformation("[CoopHub] Invalid JWT: {Message}", ex.Message);
            return null;
        }
    }
}

internal class LobbyRoom
{
    public string Code { get; }
    public ConcurrentDictionary<Guid, ConnectionEntry> Connections { get; } = new();

    public LobbyRoom(string code)
    {
        Code = code;
    }
}

internal class ConnectionEntry
{
    public WebSocket Socket { get; }
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    /// <summary>
    /// Set to true after the initial hello → welcome handshake completes.
    /// Bus broadcasts skip connections that aren't ready, so a broadcast
    /// can't race with the welcome write to the same socket.
    /// </summary>
    public bool Ready { get; set; }

    // --- Phase 6: rate limit (token bucket, 10 tokens cap, 10/sec refill) ---

    /// <summary>Current rate-limit token count. Starts full.</summary>
    public double Tokens { get; set; } = ClearRateCapacity;

    /// <summary>Timestamp of the last token bucket refill.</summary>
    public DateTime LastRefillAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// First time an over-rate drop was observed since the last normal rate.
    /// Cleared once the bucket replenishes. If sustained past
    /// <see cref="ClearRateSustainedDisconnectSeconds"/> seconds, the
    /// connection is force-disconnected.
    /// </summary>
    public DateTime? OverRateSince { get; set; }

    public const double ClearRateCapacity = 10.0;
    public const double ClearRateRefillPerSecond = 10.0;
    public const double ClearRateSustainedDisconnectSeconds = 5.0;

    // --- Phase 6: heartbeat / activity tracking (AFK logic is Phase 7) ---

    public DateTime LastHeartbeatAt { get; set; } = DateTime.UtcNow;
    public bool Focused { get; set; } = true;
    public DateTime LastInputAt { get; set; } = DateTime.UtcNow;

    public ConnectionEntry(WebSocket socket)
    {
        Socket = socket;
    }
}
