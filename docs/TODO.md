# Phase 8 — Completion, results, replay v3, retention

Close the loop on the co-op feature set. On `lobby_completed`, show a results screen; allow replaying the full session (unified replay with per-player attribution); run retention jobs that strip old snapshots and soft-delete idle lobbies.

## Open design decisions (resolve before implementing)

1. **Results screen UX.** Reuse the solo `VictoryController` layout (centered modal, "Play Again" / "Menu" / "Replay" buttons) or build a new `CoopResultsScreen` that leans on the sidebar's roster style (full table)?
   - **Proposal**: new component. The sidebar already has the roster rendering; a results screen just needs the full table + action buttons.
2. **Play Again semantics.** Does it return to the hub, re-host a new lobby with the same settings, or something else?
   - **Proposal**: returns to the Coop Hub (same as Menu does today in co-op). Re-hosting with same settings would be nice-to-have; defer.
3. **Replay v3 roster header.** Full list of all registered players, or only those who cleared at least one arrow?
   - **Proposal**: all registered. Spectators who never clicked should still show in the roster.
4. **Replay v3 backward compat.** Does `LeaderboardManager` / `ReplayPlayer` need to load v3 solo replays? If solo keeps writing v2, no. If solo keeps writing v2 forever, v3 is a strict-additive co-op-only schema.
   - **Proposal**: solo keeps writing v2. v3 is co-op-exclusive (server-generated from `LobbyEvents` on demand). `ReplayPlayer` must decode both. `ReplayData` gains an optional `Roster` field that solo leaves null.
5. **Retention job cadence.** Daily at 03:00 UTC? Configurable?
   - **Proposal**: background `IHostedService` with a 24 h `PeriodicTimer`, first fire 15 minutes after startup (so a restart doesn't skip a day). Configurable via `Coop:RetentionRunTime` as HH:mm; default empty (just "24 h from startup"). Can be tightened later.
6. **Retention window.** 30 days for both snapshot-strip and idle-reap?
   - **Proposal**: yes. `Coop:SnapshotStripAfterDays` and `Coop:IdleReapAfterDays`, default 30.
7. **Idle-reap scope.** Active only, or also Generating / GenerationFailed?
   - **Proposal**: Active + GenerationFailed. Generating should time out via the worker, not a retention job.
8. **Server-side replay cache.** Should `GET /api/lobbies/{code}/replay` regenerate on every call from `LobbyEvents`, or cache the built v3 blob on `lobby_completed` and serve it?
   - **Proposal**: build-on-demand v1. Cache only if traffic shows it's expensive. `LobbyEvents` query with the snapshot is O(event count) which is bounded by board size.

## Architecture

### Server

**Results are implicit.** No new broadcast needed on top of `lobby_completed` + roster patches; the client builds the results table from the last `roster_full` / `roster_patch` state it has.

**New REST**
- `GET /api/lobbies/{code}/replay` — registered players of a lobby can fetch the v3 replay. Returns 404 if lobby missing / snapshot stripped, 403 if caller isn't registered. Response body is the serialized `ReplayData` (v3 JSON).

**Replay construction (`LobbyReplayService`)**
- Loads snapshot blob → decodes board layout (initial state).
- Loads every `LobbyEvent` for the lobby ordered by `Seq`.
- Loads every `LobbyRegistration` for the lobby → builds roster header.
- Emits `ReplayData` with: `initialSnapshot`, `roster: List<ReplayRosterEntry>`, `events: List<ReplayEvent>` (with `PlayerId`), `finalTime` (max across players).

**Retention (`server/ArrowThing.Server/Coop/Retention/`)**
- `RetentionBackgroundService : IHostedService` — owns a `PeriodicTimer`. Runs `SnapshotStripperJob` + `IdleLobbyReaperJob` sequentially.
- `SnapshotStripperJob` — `WHERE Status IN (Completed, Deleted) AND (CompletedAt < cutoff OR DeletedAt < cutoff) AND SnapshotStrippedAt IS NULL`. Nulls `LobbySnapshots.Data`, stamps `SnapshotStrippedAt = now`. Batched to 50 lobbies per tick to keep DB impact bounded.
- `IdleLobbyReaperJob` — `WHERE Status IN (Active, GenerationFailed) AND LastActivityAt < cutoff`. Transitions to `Deleted`, broadcasts `disconnect { reason: "idle" }` to any lingering connections, sets `DeletedAt = now`.
- Both jobs log counts per tick; no-op when no rows match.
- Feature-flagged: `Coop:EnableRetention` default `false`; flip to `true` after verifying the queries against prod-like data.

### Client

**Replay v3 decoding**
- `ReplayEvent.cs` — add nullable `PlayerId` (Guid?). Existing fields unchanged.
- `ReplayData.cs` — add `Version` (already exists?), `Roster: List<ReplayRosterEntry>?` (nullable; solo replays leave null).
- `ReplayRosterEntry.cs` — `{ PlayerId, DisplayName, Color }`.
- `ReplayPlayer` — handles v2 + v3. When v3, applies `PlayerId` to each event for attribution rendering.
- `ReplayVerifier` — ignores `Roster` and per-event `PlayerId` for verification; the tap sequence alone determines clearability.

**Results screen (`CoopResultsScreen.cs`)**
- Built in code (no UXML). Mounted as overlay in `GameController` on `CoopSession.LobbyCompleted`.
- Full roster table sorted by `ClearCount DESC, AccumulatedMillis ASC`. Own row highlighted. Medals for top 3 (reuse solo leaderboard styling).
- Actions: `Play Again` → `SceneNav.Replace("CoopHub")`; `Menu` → `SceneNav.Push("MainMenu")` (or pop depending on stack); `View Replay` → fetch v3 replay via `ApiClient.GetLobbyReplayAsync`, save locally via `LeaderboardManager`, enter Replay scene.

**Read-only post-completion**
- After `LobbyCompleted`, `GameController` sets `_inputHandler.SetInputEnabled(false)` (already does partially). Clicks do nothing.
- Sidebar stays visible but no longer updates.

**Replay playback**
- `ReplayViewController` existing path loads `ReplayData`. If v3 (`Roster != null`):
  - Spawn a stripped `CoopSidebar`-style panel showing the roster as reconstructed at playback time.
  - Tap indicators use the clearer's color from the event's `PlayerId → Roster` lookup.
  - Arrow pull-out flashes use the clearer's color (same tint path as live play).

**ApiClient**
- `GetLobbyReplayAsync(string code) → ApiResult<ReplayData>`.

## Implementation stages

1. **Server replay service + endpoint**: `LobbyReplayService`, `GET /api/lobbies/{code}/replay`, auth check (caller must be registered).
2. **Replay format v3**: add nullable `PlayerId` to `ReplayEvent`, add `Roster` + `ReplayRosterEntry` to `ReplayData`. `ReplayPlayer` + `ReplayVerifier` handle both versions.
3. **Client fetch + playback**: `ApiClient.GetLobbyReplayAsync`, `ReplayViewController` colorizes v3 events, roster panel appears during playback.
4. **Results screen**: `CoopResultsScreen` mounts on `LobbyCompleted`. Buttons for Play Again / Menu / View Replay.
5. **Retention jobs**: `RetentionBackgroundService` + `SnapshotStripperJob` + `IdleLobbyReaperJob`, gated behind `Coop:EnableRetention`.
6. **Tests**:
   - EditMode: `ReplayV3Tests` (encode/decode round-trip, v2 back-compat, roster lookup).
   - xUnit: full lifecycle (create → gen → clear → complete → GET replay → v3 shape), retention job time-travel tests.
   - Manual: two-client flow through completion, inspect results, launch replay.
7. **Docs + PR**: CoopRoadmap Phase 8 marked Implemented, TechnicalDesign updated, TODO.md deleted.

## Manual test cases

1. Clear every arrow in a 20×20 lobby; results screen appears with full table; own row highlighted; top 3 tinted gold/silver/bronze.
2. "Play Again" returns to the Coop Hub.
3. "View Replay" loads the replay scene. Playback shows both players' clears in their respective colors; roster panel updates as taps land.
4. Another tab that was offline during completion sees the results screen when they reconnect (lobby status reads Completed → hub sends `disconnect { reason: "lobby_completed" }`... actually the P6 code closes the socket; revisit — probably just don't auto-enter the game scene when status is Completed).
5. Retention dry-run: manually invoke `SnapshotStripperJob` with `CompletedAt` set to 31 days ago; verify `LobbySnapshots.Data` is nulled.
6. Retention dry-run: manually invoke `IdleLobbyReaperJob` on an `Active` lobby with `LastActivityAt` 31 days ago; verify status → `Deleted`.
