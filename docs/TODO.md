# Phase 8 — Completion, results, unified replay (v6), retention

Close the loop on the co-op feature set. On `lobby_completed`, show a results screen that persists even for reconnectors to a Completed lobby; extend the solo replay format additively to carry per-player attribution; run retention jobs that strip old snapshots and soft-delete idle lobbies.

## Design decisions (resolved)

1. **Results screen UX.** New `CoopResultsScreen` component; leans on sidebar row styling (color dot, name, timer, clear count). Full roster table with top-3 medal tints.
2. **Play Again** → return to Coop Hub. No one-click rematch in v1.
3. **Roster header** includes every registered player (not just clearers) so spectators render.
4. **Unified replay format (v6)**, not a separate v3. Solo stays compatible — `Roster` and per-event `PlayerId` are nullable additive fields. `ReplayData.version` bumps to 6. Solo continues producing v6 with `Roster = null`; co-op produces v6 with a populated roster.
5. **Retention cadence.** Background `IHostedService` with a 24 h `PeriodicTimer`, first fire 15 minutes after startup. No per-hour scheduling in v1.
6. **Retention windows.** 30 days for both snapshot-strip and idle-reap. Hardcoded constants (`SnapshotStripAfterDays = 30`, `IdleReapAfterDays = 30`) — revisit if we need tuning.
7. **Idle-reap scope.** Active + GenerationFailed. Generating is handled by the worker's own timeout.
8. **Server-side replay cache.** Build on demand from `LobbySnapshots` + `LobbyEvents`. No caching until traffic shows it matters. ASP.NET `ResponseCompression` (when/if the gzip storage PR lands on main) will handle wire compression.
9. **Reconnecting to a Completed lobby.** Fetch the v6 replay via REST and show the results screen (no WS; the lobby is Completed). Lets a player who missed the finish still see the tallies.

## Inherited bits worth noting

- Solo `ReplayData` is currently **v5** (base64 BinarySnapshot blob, single canonical encoder shared with co-op wire). v6 is strictly additive.
- Main does not yet have the dedicated gzip replay storage column (`ReplayJsonGz`) from PR `claude/phase-4-storage-perf` (48f7d79). When it lands, co-op replays benefit without code changes.

## Architecture

### Server

**Results are implicit.** No new broadcast needed on top of `lobby_completed` + roster patches; the client builds the results table from the last `roster_full` / `roster_patch` state it has.

**New REST**
- `GET /api/lobbies/{code}/replay` — registered players of a lobby can fetch the v3 replay. Returns 404 if lobby missing / snapshot stripped, 403 if caller isn't registered. Response body is the serialized `ReplayData` (v3 JSON).

**Replay construction (`LobbyReplayService`)**
- Loads snapshot blob → decodes board layout (initial state), re-encodes as `ReplayData.boardSnapshot` (base64 of BinarySnapshot arrows).
- Loads every `LobbyEvent` for the lobby ordered by `Seq`. Maps `ClearAccepted` rows to `ReplayEvent { type: "clear", tapX, tapY, playerId, timestamp }` and prepends/appends synthetic `session_start` + `end_solve` events so the solo replay pipeline (`ReplayPlayer` / `ReplayVerifier`) runs without branch-per-format conditionals.
- Loads every `LobbyRegistration` for the lobby → builds `Roster: List<ReplayRosterEntry>` with `{ PlayerId, DisplayName, Color }`.
- Emits `ReplayData { version: 6, gameId: lobby.Id, seed, boardWidth, boardHeight, maxArrowLength, boardSnapshot, roster, events, finalTime }`. `finalTime` is the max `AccumulatedMillis` across the roster (session length, not per-player time).

**Retention (`server/ArrowThing.Server/Coop/Retention/`)**
- `LobbyRetentionService : BackgroundService` — 24 h `PeriodicTimer`, first fire 15 min after startup. Runs `SnapshotStripperJob` + `IdleLobbyReaperJob` sequentially.
- `SnapshotStripperJob` — `WHERE Status IN (Completed, Deleted) AND (CompletedAt < cutoff OR DeletedAt < cutoff) AND SnapshotStrippedAt IS NULL`. Nulls `LobbySnapshots.Data`, stamps `SnapshotStrippedAt = now`. Batched to 50 lobbies per tick.
- `IdleLobbyReaperJob` — `WHERE Status IN (Active, GenerationFailed) AND LastActivityAt < cutoff`. Transitions to `Deleted`, broadcasts `disconnect { reason: "idle" }` to any lingering connections via `CoopHub`, sets `DeletedAt = now`.
- Both jobs log counts per tick; no-op when no rows match.
- No feature flag. Runs always. Constants: `SnapshotStripAfterDays = 30`, `IdleReapAfterDays = 30`.

### Client

**Replay v6 decoding**
- `ReplayEvent.cs` — add nullable `playerId` (Guid?). Existing fields unchanged. `[JsonProperty(NullValueHandling = Ignore)]` so solo replays don't bloat.
- `ReplayData.cs` — bump `version = 6`. Add `roster: List<ReplayRosterEntry>` with same null-ignore attribute. Solo replays stay at v5 on-disk shape (no roster, no playerId); v6 is a strict superset.
- `ReplayRosterEntry.cs` (new) — `{ PlayerId (Guid), DisplayName (string), Color (string hex) }`.
- `ReplayPlayer` — no behavior change needed for tap playback; it reads events regardless of version. For co-op attribution rendering, the replay viewer looks up `PlayerId → Roster` and tints the tap indicator + pull-out flash.
- `ReplayVerifier` — ignores `roster` and `playerId`. Tap sequence + timestamps alone determine clearability (unchanged).

**Results screen (`CoopResultsScreen.cs`)**
- Built in code (no UXML). Mounted as overlay in `GameController` on `CoopSession.LobbyCompleted`.
- Full roster table sorted by `ClearCount DESC, AccumulatedMillis ASC`. Own row highlighted. Medals for top 3 (reuse solo leaderboard styling).
- Actions: `Play Again` → `SceneNav.Replace("CoopHub")`; `Menu` → `SceneNav.Push("MainMenu")` (or pop depending on stack); `View Replay` → fetch v3 replay via `ApiClient.GetLobbyReplayAsync`, save locally via `LeaderboardManager`, enter Replay scene.

**Read-only post-completion**
- After `LobbyCompleted`, `GameController` sets `_inputHandler.SetInputEnabled(false)` (already does). Clicks do nothing.
- Sidebar stays visible but no longer updates.
- Results screen overlays the board.

**Reconnecting to a Completed lobby**
- When the Coop Hub's "Play" button is clicked for a `Completed` lobby, `GameController` skips the WS game-state flow and instead calls `ApiClient.GetLobbyReplayAsync(code)` to fetch the v6 replay. It builds a read-only `Board` from the initial snapshot, applies every `clear` event to get the final empty state (or stops partway for an in-progress deterministic preview), populates `CoopSession.Roster` from the replay's roster header, and mounts the results screen directly.
- Effectively: "opening a Completed lobby takes you to the results screen."

**Replay playback**
- `ReplayViewController` existing path loads `ReplayData`. If v3 (`Roster != null`):
  - Spawn a stripped `CoopSidebar`-style panel showing the roster as reconstructed at playback time.
  - Tap indicators use the clearer's color from the event's `PlayerId → Roster` lookup.
  - Arrow pull-out flashes use the clearer's color (same tint path as live play).

**ApiClient**
- `GetLobbyReplayAsync(string code) → ApiResult<ReplayData>`.

## Implementation stages

1. **Replay format v6**: add nullable `playerId` to `ReplayEvent`, `roster` + `ReplayRosterEntry` to `ReplayData`. Bump version. Domain layer only — no consumers yet.
2. **Server replay service + endpoint**: `LobbyReplayService.BuildAsync(lobby)` → `ReplayData`. `GET /api/lobbies/{code}/replay` authorizes registered players, returns v6 JSON. Handles stripped snapshots (404 with a clear error).
3. **CoopResultsScreen**: mounts on `LobbyCompleted`. Sorted table, medals, Play Again / Menu / View Replay buttons.
4. **Reconnect-to-Completed flow**: Coop Hub "Play" on a Completed lobby routes `GameController` into a replay-backed results-only mode (no WS).
5. **Replay playback (v6)**: `ReplayViewController` tints tap indicators + pull-out by `playerId → Color` lookup. Adds a minimal roster panel that reconstructs state at playback time (running per-player clear count + latest timestamp).
6. **Retention jobs**: `LobbyRetentionService : BackgroundService` + `SnapshotStripperJob` + `IdleLobbyReaperJob`.
7. **Tests**:
   - EditMode: `ReplayV6Tests` (encode/decode round-trip, v5 back-compat for old solo saves, roster lookup, null-ignored fields).
   - xUnit: full lifecycle (create → gen → clear to completion → GET replay → v6 shape + roster + event playerIds), retention job time-travel (stripper + reaper), registration auth on the replay endpoint.
   - Manual: two-client flow through completion, inspect results, launch replay, reopen a Completed lobby from the hub → lands on results.
8. **Docs + PR**: CoopRoadmap Phase 8 marked Implemented, TechnicalDesign updated, TODO.md deleted.

## Manual test cases

1. Clear every arrow in a 20×20 lobby; results screen appears with full table; own row highlighted; top 3 tinted gold/silver/bronze.
2. "Play Again" returns to the Coop Hub.
3. "View Replay" loads the replay scene. Playback shows both players' clears in their respective colors; roster panel updates as taps land.
4. Another tab that was offline during completion sees the results screen when they reconnect (lobby status reads Completed → hub sends `disconnect { reason: "lobby_completed" }`... actually the P6 code closes the socket; revisit — probably just don't auto-enter the game scene when status is Completed).
5. Retention dry-run: manually invoke `SnapshotStripperJob` with `CompletedAt` set to 31 days ago; verify `LobbySnapshots.Data` is nulled.
6. Retention dry-run: manually invoke `IdleLobbyReaperJob` on an `Active` lobby with `LastActivityAt` 31 days ago; verify status → `Deleted`.
