# Phase 7 — Sidebar + per-player attribution + timer + toasts

Make co-op **feel** like co-op: live roster, colors on tap indicators and clear animations, per-player timers, toasts.

## Design decisions (resolved)

1. **Roster transport — incremental.** Server sends `roster_full` on hello and `roster_patch` (only changed/added/removed entries) on subsequent changes. Scales to hundreds of players without multi-MB broadcasts.
2. **Server-authoritative clear counts.** Every `cleared` payload carries the clearer's new `clearCount` + `color` + `displayName`. No client-side lookup.
3. **Disconnected players stay visible** (dimmed) until lobby expiry. Offline detection via WebSocket close + heartbeat watchdog (~30 s without heartbeat → mark offline + broadcast patch).
4. **No AFK tracking in Phase 7.** Drop it entirely; just online/offline. De-facto spectators are a feature, not an exception worth styling. Revisit if a future mode needs it.
5. **Sidebar shows top 10 by clear count** (pinned own row always visible even if outside top 10) + a "Show all (N)" button that opens a scrollable modal with the full roster. Narrow viewport collapses sidebar into a player-count pill.
6. **Only clears propagate.** Non-clearable taps are already filtered client-side (Phase 6 follow-up); blocked-tap attribution deferred to future no-failed-clears mode.
7. **Remove the HUD timer in co-op.** Each player sees their own elapsed time in their sidebar row. No shared/global session timer.

## Architecture

### Server

**New/changed `CoopHub` messages**
- `roster_full { players: [...] }` — sent once per connection immediately after `snapshot`. Full list of all registered-for-lobby users with current state.
- `roster_patch { upsert: [...], remove: [userId] }` — broadcast on any roster change. Field-level diffing is overkill; we send the full updated entry for any changed user. Throttle: max 1 patch / 500 ms per lobby (accumulate pending changes, flush on a timer).
- `cleared` payload extended: add `newClearCount: int`, `color: string`, `displayName: string`. (`playerId` already present.) Client applies counts locally and uses color for the tap indicator + clear flash.
- `timer_update { accumulatedMillis }` (client → server). Server persists to `LobbyRegistrations.AccumulatedMillis` + stamps `LastActivityAt`. Triggers a patch with the sender's updated time.

**Server state**
- `LobbyRegistrations` columns we already have: `UserId`, `DisplayNameAtJoin`, `ColorAtJoin`, `ClearCount`, `AccumulatedMillis`, `FirstClearAt`, `JoinedAt`, `LastActivityAt`. Good — no schema migration needed.
- On accepted clear: `registration.ClearCount++`, set `FirstClearAt` if null. Echo new count on `cleared` + queue a roster patch.
- On `timer_update`: validate `accumulatedMillis` is non-decreasing (prevent time-travel), persist, queue patch.
- On connect / disconnect: queue patch with the player's `online` flag flipped.

**Watchdog for zombie connections**
- Each `ConnectionEntry` gains a `LastHeartbeatAt`. (Already has `LastInputAt`.)
- Background timer per lobby (or a single hub-wide sweeper): every 10 s, check all entries; if `now - LastHeartbeatAt > 30 s`, treat the connection as dead — close the socket, remove from `room.Connections`, broadcast roster patch marking offline.

### Client

**`CoopSession.Roster`**
- `IReadOnlyDictionary<Guid, CoopPlayer>` on `CoopSession`.
- `CoopPlayer { Id, DisplayName, Color (hex → Color32), ClearCount, AccumulatedMillis, Online, IsLocal }`.
- Events: `RosterUpdated` (fires once per `roster_full` / `roster_patch`).
- On `cleared` handler: before firing `RemoteCleared`, apply the payload's `newClearCount` + `color` + `displayName` to the roster entry (in case the patch hasn't arrived yet).

**`CoopPlayerTimer.cs` (new, view layer)**
- Local-only. Tracks `AccumulatedMillis` for the local player.
- Starts ticking on first `CoopSession.TrySubmitClear` that actually sends (i.e. first accepted clear-attempt).
- Ticks in `Update()` using `Time.unscaledDeltaTime * 1000`.
- Stops ticking on `Application.isFocused == false` OR tab hidden via `visibilityChange` (WebGL). Resumes on refocus.
- Every 5 s (or 100 ms after an accepted clear, to keep the sidebar snappy): emits `timer_update { accumulatedMillis }` via `CoopClient.SendAsync`.
- Stops permanently on `LobbyCompleted` or `Dispose`.

**`CoopSidebar.cs` (new, view layer)**
- UIToolkit, attached into `GameController`'s co-op-mode UI tree.
- Data-bound to `CoopSession.Roster`.
- Row: `[color dot] [display name] [MM:SS] [clear count]`. Pinned self row at top; top 9 others sorted by `ClearCount DESC, AccumulatedMillis ASC`. If more than 10 players, show a `Show all (N)` button that opens a modal with the full roster.
- Offline rows dim to 40 % opacity with a "(offline)" suffix.
- Narrow viewport (< 768 px panel width): collapses into a player-count pill top-right. Tapping the pill opens the all-players modal.
- Re-renders on `RosterUpdated`. Efficient: only diffs the player rows, not the whole list.

**`CoopAllPlayersModal.cs`** — thin overlay that reuses `.list-screen` / `.list-scroll` styling from `Shared.uss`. Scrollable ListView-style, same row template as sidebar minus the "..." truncation.

**Tint plumbing**
- `TapIndicatorPool.Spawn(Vector3 worldPos, Color color)` — adds color param. Existing solo callers wrap with white (clear) / red (reject) constants.
- `ArrowView.ClearAnimated(Color? flashColor = null)` — brief tint flash (150 ms) before pull-out when color set.
- `BoardView.ClearArrowAnimated(arrow, Color? flashColor = null)` — plumbs through to ArrowView.

**`GameController` co-op-mode changes**
- **Skip timer HUD entirely** in co-op mode. Solo path unchanged.
- Instantiate `CoopSidebar` and `CoopPlayerTimer`; wire `CoopSession.RosterUpdated` → sidebar re-render.
- On `CoopSession.RemoteCleared`:
  - If `!evt.IsLocal`: `boardView.ClearArrowAnimated(evt.Arrow, evt.Color)` + `tapIndicatorPool.Spawn(evt.TapWorld, evt.Color)`.
  - Apply `newClearCount` to roster.
- Toasts:
  - `player_joined` (derived from roster diff): `{name} joined`. Skip for the local player.
  - Rate-limited (`rejected_rate`): "Slow down" toast, throttled to once per 3 s.
  - Reconnect-failed after retry budget: "Reconnecting..." during backoff, "Lost connection" after giving up.

## Wire format

```
server → client:  welcome, snapshot, <binary snapshot>, roster_full, roster_patch*, cleared+, lobby_completed
client → server:  hello, clear_attempt, timer_update, heartbeat
private rejects:  rejected_race, rejected_dep, rejected_rate (unchanged from Phase 6)
```

## Implementation stages

1. **Server protocol**: `roster_full` on connect, `roster_patch` with 500 ms throttle on change, extended `cleared` payload, `timer_update` handler with monotone-check, heartbeat watchdog.
2. **Client roster state**: `CoopSession.Roster` + events, parse `roster_full`/`roster_patch`, apply `cleared` payload fields.
3. **Tint plumbing**: TapIndicatorPool + ArrowView color params; wire from `RemoteCleared` handler.
4. **Sidebar + modal UI** with narrow-layout pill.
5. **CoopPlayerTimer** with focus-based pause, 5 s emit.
6. **Toasts**: player_joined diff, rate-limited, reconnect-failed.
7. **Tests**
   - xUnit: roster_full shape on hello, roster_patch on clear, cleared payload carries new fields, timer_update monotone rejection, heartbeat watchdog marks offline.
   - Unity EditMode: `CoopPlayerTimer` focus-loss pause/resume with fake clock.
   - Manual test cases below.
8. **Docs**: Phase 7 `Implemented` note in CoopRoadmap, update TechnicalDesign co-op section, delete TODO.md.

## Manual test cases

1. Two clients, distinct colors, both visible in sidebar with correct dots.
2. Client A clears an arrow; both clients see A's clear count increment.
3. Client B sees A's clear animation flash in A's color + a tap indicator in A's color.
4. Client A's timer ticks in the sidebar during active play; pauses when A's tab blurs.
5. Client A closes tab: within 30 s A's row shows (offline) dimmed. Row stays in the list.
6. Client A reconnects: row re-colors to online.
7. Narrow viewport: sidebar collapses into pill; tap opens full modal.
8. 12 clients in a lobby: top 10 shown, "Show all (12)" button opens full modal. Own row is pinned at top even if outside top 10.
9. No HUD timer visible in co-op mode.
10. Solo mode HUD timer still works normally (regression check).
