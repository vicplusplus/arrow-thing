# Phase 7 — Sidebar + per-player attribution + timer + toasts

Make co-op **feel** like co-op: live roster, colors on tap indicators and clear animations, AFK-aware timer, toasts.

Building on Phase 6:
- `CoopSession` already exists and routes `cleared` / `rejected_race` / `lobby_completed`. Add `Roster` state + `RosterUpdated` event.
- Non-clearable taps filtered client-side — no broadcast — so tap propagation only covers accepted clears. Simpler than the original Phase 7 plan.
- GameController carries co-op mode (no separate scene). Sidebar lives there.

## Open design decisions (resolve before implementing)

1. **Roster transport.** Single `roster` broadcast with the full list, or incremental `player_joined` / `player_left` + `player_updated` for field changes? Pick one.
   - **Proposal**: full `roster` on every change. Simpler; payload is tiny (≤ 50 entries × ~100 bytes).
2. **Disconnected players.** Show greyed-out in sidebar or hide until reconnect? The 60 s eviction grace means they may reconnect.
   - **Proposal**: show with dim/offline indicator; full remove only on explicit leave (if we ever add that) or lobby expiry.
3. **Clear count authority.** Client-increments-on-RemoteCleared vs. server-ships-count-on-cleared.
   - **Proposal**: server authoritative. Every `cleared` payload carries the clearer's new `clearCount`; simpler and resilient to dropped messages.
4. **Timer AFK pause trigger.** Focus-loss OR 60 s idle, OR only focus-loss?
   - **Proposal**: BOTH — pause on `visibilitychange` hidden OR 60 s since last input. Resume on focus + any input.
5. **Sidebar visibility toggle.** Always-visible panel with collapse, or button-only-opens?
   - **Proposal**: always-visible on wide screens (≥ 768 px wide), player-count pill on narrow. Collapse button on wide screens.

## Architecture

### Server
- **`CoopHub` additions**
  - New `ConnectionEntry` fields: already have `Focused`, `LastInputAt`. Add `ClearCount` to `LobbyRegistration` (DB column already exists from Phase 3 schema? verify). Add `AccumulatedMillis` (already exists).
  - On connect + registration: broadcast full `roster` with all registered-for-lobby users and their current state.
  - On disconnect: broadcast `roster` (marks player offline; doesn't delete registration).
  - On accepted clear: increment `LobbyRegistration.ClearCount`, update `FirstClearAt` if null. Include the clearer's current counts in a follow-up `roster` broadcast (piggybacked after the `cleared` event) OR on the cleared payload itself.
    - **Decision**: put `newClearCount` + `color` + `displayName` on the `cleared` payload. Avoids a second round-trip for the common case.
  - New message handler: `timer_update { accumulatedMillis, firstClearAt }`. Persists to registration, refreshes LastActivityAt. Echoed in next roster.
  - Heartbeat handler already stamps `Focused` + `LastInputAt`. On roster rebuild, AFK = `!Focused || (now - LastInputAt) > 60 s`.
  - Throttle roster broadcasts: at most once every 500 ms per lobby. Accumulate pending changes; flush on a timer.

- **Wire envelopes (new in Phase 7)**
  - `roster` (broadcast): `{ players: [{ userId, displayName, color, clearCount, accumulatedMillis, afk, online }] }`
  - `timer_update` (client→server): `{ accumulatedMillis }` (server takes `now - joinedAt` for FirstClearAt implicitly)
  - `player_joined` / `player_left`: **skip in favor of full roster rebroadcast.**
  - `cleared` payload gains: `newClearCount`, `color`. Existing `playerId` stays. (Bumps payload size ~30 bytes per broadcast.)

### Client
- **`CoopSession.Roster`** — `IReadOnlyDictionary<Guid, CoopPlayer>`. `CoopPlayer { Id, DisplayName, Color (hex string → Color32), ClearCount, AccumulatedMillis, Afk, Online }`. `RosterUpdated` event fires on every roster broadcast.
- **`CoopPlayerTimer.cs` (new, view layer)** — tracks local player's `AccumulatedMillis`. Starts on first local clear-attempt submission (listen via `CoopSession` or inject callback). Pauses on `Application.isFocused == false` OR 60 s since last `InputHandler.LastInputTimeUtc`. Ticks in `Update`. Emits `timer_update` every 5 s via `CoopClient`.
- **`CoopSidebar.cs` (new)** — UIToolkit component built in code (or UXML, TBD). Subscribes to `Roster`. Re-renders on update. Pinned own row at top; others sorted by `ClearCount DESC`. Each row: color dot + name + clear count + timer (MM:SS). Afk rows dimmed with "(afk)" suffix. Offline rows dimmed.
- **`CoopSidebar.uxml` + `CoopSidebar.uss`** — or stylesheets in existing `Assets/UI/Coop/Coop.uss`.
- **Narrow layout** — when panel width < 768 px, sidebar collapses into a player-count pill top-right (shows count + highest-rank indicator). Pill tap opens full-screen list overlay. Reuses the Coop Hub list styling where possible.
- **`TapIndicatorPool.Spawn`** — gains optional `Color` tint parameter. Existing solo callers unchanged (null = white for clears, red for rejects).
- **`ArrowView.ClearAnimated`** — gains optional `Color?` flashColor; 150 ms tint-flash before the pull-out when set.
- **`GameController` co-op mode**
  - Instantiate `CoopSidebar`, wire `CoopSession.RosterUpdated` → `CoopSidebar.Render`.
  - Instantiate `CoopPlayerTimer`, wire to `InputHandler.LastInputTimeUtc` and `CoopClient.SendAsync(timer_update)` every 5 s.
  - On `CoopSession.RemoteCleared`:
    - If `!evt.IsLocal`, play `ClearArrowAnimated(evt.Arrow, flashColor: evt.PlayerColor)`.
    - Also spawn a tap indicator in `evt.PlayerColor` at `evt.TapWorld`.
  - Toasts:
    - `player_joined` (diff from previous roster to new) → "Alice joined"
    - `lobby_completed` → existing toast already in place; change to use display name.
    - Rate-limited (server `rejected_rate`) → "Slow down" toast.
    - Reconnect failed after N attempts (already exists but as log) → promote to toast.

## Implementation stages

1. **Server protocol**: add `roster` broadcast on connect/disconnect/timer_update/accepted-clear. Extend `cleared` payload with `newClearCount` + `color` + `displayName`. Add `timer_update` handler. Throttle roster flushes.
2. **Client roster state**: `CoopSession.Roster` + `RosterUpdated`. Parse inbound `roster`. Apply server-authoritative `newClearCount`/`color` on `cleared`.
3. **Tint plumbing**: `TapIndicatorPool.Spawn(color)`, `ArrowView.ClearAnimated(flashColor)`, `CoopSession.ClearedEvent.Color`. Wire through remote-cleared handler.
4. **Sidebar UI**: `CoopSidebar.cs` + USS. Mount in `GameController` co-op mode. Narrow layout pill fallback.
5. **Timer**: `CoopPlayerTimer.cs` with AFK pause. 5 s `timer_update` emission. Sidebar displays per-player elapsed.
6. **Toasts**: diff-based join toast; promote reconnect-failed.
7. **Tests**
   - xUnit: `timer_update` round-trip, `roster` broadcast shape, accepted-clear payload carries new fields.
   - Unity EditMode: `CoopPlayerTimer` AFK pause/resume logic with fake clock.
   - Manual: two tabs/accounts, verify sidebar rows update live, colors match, AFK indicator when one tab blurs, disconnect indicator when one closes then 60 s eviction.
8. **Docs**: update `CoopRoadmap.md` Phase 7 with implemented notes; update `TechnicalDesign.md` `Co-op Server` section.

## Manual test cases

1. **Two clients, colors differ.** Both log in with distinct `CoopColor` values; sidebar shows both rows with correct dots.
2. **Clear count increments.** Client A clears an arrow; both clients see A's count go up.
3. **Timer per-player.** Client A clears first; A's timer starts ticking. Client B is idle; B's timer stays at 00:00.
4. **AFK detection.** Client A goes idle (focus loss) for > 1 s; within 500 ms the heartbeat + roster broadcast flags A as AFK. When A returns (focus + tap), the flag clears.
5. **Disconnect offline indicator.** Client A closes tab; within 2 s roster marks A offline. 60 s later, state evicts — A still shown as offline until lobby completion.
6. **Remote clear tint.** Client A clears; client B sees the arrow pull-out flash in A's color.
7. **Narrow viewport.** Resize < 768 px; sidebar collapses into pill; pill shows correct count; tap opens list overlay.
8. **Player joined toast.** B joins after A is already connected; A sees a transient "B joined" toast.
