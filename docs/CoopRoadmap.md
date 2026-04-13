# Co-op Roadmap

Design and phased implementation plan for co-op boards — shared persistent puzzles that any number of registered players can chip away at, in real time when they overlap and asynchronously when they don't.

This document is the single source of truth for co-op design decisions until the feature ships. Each phase below corresponds to its own PR. When a phase lands, its section is updated with an "Implemented" note and the `docs/TechnicalDesign.md` is updated to match. Small per-phase `docs/TODO.md` files may be used for the phase in progress (per the feature workflow in `CLAUDE.md`) and deleted when the phase merges.

## Overview

A **co-op lobby** is a persistent "networked board save" with a unique join code. Any logged-in, email-verified player can register to a lobby via its code and contribute to clearing the board. The board persists server-side; players can come and go freely. When two or more players are online at once, they share a real-time WebSocket session and see each other's clears live. When the last arrow is cleared, the lobby finalises into a read-only results screen showing everyone's individual clear count and time.

The feature is deliberately **cooperative-social, not competitive-ranked**. There is no global co-op leaderboard — only per-lobby results. Co-op is a way to share the puzzle experience with friends, not a new ladder to climb.

## Goals

- **Persistent shared boards** that outlive any individual play session. Join code shareable via URL.
- **Real-time collaboration** when players overlap, with optimistic local animation and server-authoritative validation.
- **Per-player attribution** — each registered player has their own timer and clear count, displayed in a live sidebar during play and on the results screen at completion.
- **Reuses existing infrastructure** wherever possible: generation algorithm, domain types, replay format, auth/JWT, settings/themes.
- **Scales eventually** to large boards (1000×1000 is the aspirational target), but **v1 ships at a fixed 200×200** — the architecture is designed for scale while the initial implementation stays tractable.

## Non-goals (v1)

- **No global co-op leaderboard.** Per-lobby results only.
- **No PvP / racing.** Co-op is cooperative; per-player stats are for flavor, not ranking.
- **No spectator mode.** Entering a lobby auto-registers.
- **No voice / text chat.** Out of scope.
- **No mobile push / offline email notifications** on lobby events. Toasts only, in-client.
- **No guest mode.** Account (with verified email) required to create or join.
- **No lobby kicks, bans, transfers, or multi-owner.** Owner can delete the lobby; nothing else.
- **No custom board sizes.** Fixed 200×200 in v1. Size selection may come later once the rendering path is proven.

## Design Decisions

The decisions below are locked. Each was made in a dedicated design discussion before implementation began. Changes to any of these require updating this document first.

### Identity & lobby model

- **Lobby = persistent board save**, sessions are ephemeral online rooms layered on top. A lobby can exist with zero connected players.
- **One board per lobby.** Lobbies do not recycle or rotate boards; once the board is cleared, the lobby becomes read-only.
- **Fixed 200×200** board for v1.
- **Account required** (with verified email) to create or join.
- **Auto-register on entry.** Visiting a lobby registers the visitor as a player; there is no spectator mode. Creators are auto-registered at creation time.
- **Open registration until board is complete.** Players can join at any time, including during generation and after partial clears. Their clear count is naturally capped by what's left on the board when they arrive.
- **Owner = creator.** Owners can delete the lobby (see "Deletion cleanup" below). No kicks, no transfer, no moderation.
- **Join codes** are 6-char uppercase alphanumeric (e.g. `A7FK92`). Collision-checked at creation. Case-insensitive on input.
- **Share URL**: `https://arrow-thing.com/?lobby=A7FK92` deep-links into the join screen with the code pre-filled. WebGL client reads the query param on boot.
- **Lobby name**: creator-provided, 1–40 characters after trim, Unicode allowed (including emoji), not unique across lobbies, control characters stripped. Displayed in the "My lobbies" list and on the lobby screen.
- **Discovery**: code-only plus a per-player "My lobbies" list. No public browsing.
- **Per-account caps** (enforced server-side):
  - Max 5 active (incomplete) lobbies owned per account.
  - Max 50 lobbies registered to per account.
  - Max 1 concurrent generation job per account.
  - Global max 3 concurrent generation jobs across the server.

### Transport & protocol

- **WebSocket** for real-time session traffic. One connection per player per lobby.
- **One active session per lobby.** All connected players for a lobby share a single server-side room. When the last player disconnects, the session ends but the lobby persists.
- **Auto-reconnect** with exponential backoff (1s → 30s cap). Server holds the player's session slot for **60 s** after disconnect before releasing it.
- **Fresh full snapshot** on every entry or re-entry. The server sends a binary snapshot representing the current board state; after that, events stream incrementally.
- **Binary snapshot format** (versioned) used for all co-op board transfers on the wire. JSON remains the format for solo saves and replays on disk.
- **Three outcomes** for a clear attempt:
  1. **Accept** — arrow exists and is clearable. Server broadcasts a `cleared` event to all connected players, including the clearer's player id and tap position.
  2. **Reject-dep** — arrow exists but has dependencies (not clearable). Server broadcasts a `rejected` event to all connected players, including the attempter's player id and tap position. Everyone sees a red tap indicator on the attempter's tap.
  3. **Reject-race** — the arrow no longer exists (already cleared by someone else). Server sends a private `rejected_race` response only to the attempter. Other players see nothing.
- **Client-side optimism.** The clearing client plays the tap indicator and clear animation immediately on tap, without waiting for the server response. On a reject-race, the arrow has already been animated away locally; the client silently absorbs the response and does not award credit.
- **Click-rate limit**: soft cap at **10 clear attempts per second per player**. Excess dropped with a warning toast. Sustained over-rate for 5 s disconnects the player from the session with a "rate limited" reason (they can reconnect).
- **Message envelope**: JSON `{ type, seq, payload }`. `type` is the discriminator; `seq` is server-assigned monotonic for server-origin messages and client-assigned for client-origin messages. Versioning handled later if needed by adding a top-level `v` field.

### Gameplay

- **Per-player timer** and **per-player clear count**. Both tallied at the end and shown on the results screen sorted by clear count.
- **Timer starts** on the player's first clear attempt (whether successful, reject-dep, or reject-race) and **stops** when the last arrow is cleared by anyone.
- **AFK pause**: the timer pauses when the window loses focus OR when the player has produced no input (mouse move, tap, keyboard) for 60 consecutive seconds. Resumes on any input with focus regained.
- **Generation parameters** match solo: min arrow length 2, max arrow length 40. **No inspection phase** in co-op — inspection is a solo-timer concept that doesn't translate to the async model.

### HUD & attribution

- **Live player sidebar** on the right edge of the game view.
  - Rows: `[color-dot] [online-dot] [display-name] [clear-count] [timer (mm:ss)]`.
  - Default sort: **clear count DESC**. Own row pinned at top with highlight.
  - Collapsible via a button. On narrow screens (< 768 px width) the sidebar collapses into a player-count pill in the top-right corner that opens a full-screen overlay on tap.
- **Tap indicator propagation**: accepts and reject-deps propagate to all connected players with the attempter's color. Reject-race taps stay local.
- **Per-player color**: free color picker in the Account tab of Settings. Persisted server-side on the `User` record as `CoopColor`. Applied to the attempter's tap indicator and to a brief flash on the arrow during clear animation. Sidebar rows carry the color as a leading dot.
  - **Accessibility note**: the free color picker has no colorblind-safe constraints. A future enhancement can add a contrast warning against the current theme background and a colorblind-simulation preview. Tracked as an open question below.
- **All existing player settings are local-only.** Players in the same lobby can have different themes, trail visibility, drag thresholds, zoom speeds, and arrow-coloring preferences. Co-op does not propagate or lock these.
- **Toasts for critical events only** (local, not propagated by the server necessarily): player joined, generation completed, last arrow cleared, disconnected from session, rate limited, reconnect failed. No per-clear toasts — the tap indicator is the feedback channel for normal play.

### Persistence & retention

- **Strong durability during play**: every accepted clear is written to the lobby's events table in the DB **before** the server broadcasts the event. A server crash loses at most in-flight messages, never committed state.
- **Server restart recovery**: lobbies are rehydrated on demand from the initial snapshot in the DB plus the event log (replayed forward). Idle lobbies may be evicted from memory between connections.
- **Completed lobbies** become read-only and are kept **forever**. Board snapshots are stripped from storage **30 days after completion** to bound long-term storage cost. Read-only playback can always regenerate the board from the persisted seed and event log.
- **Idle TTL for active lobbies**: a lobby with no clears and no new registrations for **30 consecutive days** is automatically soft-deleted (same treatment as owner-delete). Prevents stale-lobby accumulation.

### Generation, scale, rendering

- **Server generates the board** using the existing `BoardGeneration`. Clients never regenerate; they receive a full snapshot.
- **Background generation worker**: an in-process `BackgroundService` + concurrent job queue. Lobby creation enqueues a job and returns immediately with `status: generating`. Clients subscribe to the lobby's WebSocket to receive `gen_progress` updates during generation and auto-transition to the play view when it finishes.
- **Generation failure handling**: on OOM, timeout, or repeated unsolvability, the server marks the lobby `generation_failed` and broadcasts `lobby_failed` to connected clients. The owner sees a retry/delete prompt. Failed lobbies do not auto-retry to avoid infinite loops on a bad seed.
- **Rendering path**: the existing one-GameObject-per-arrow `BoardView` is preserved unchanged for small boards (below a threshold, e.g. 3600 cells). Above the threshold, `BoardView` switches to a **viewport-culling** path that only spawns `ArrowView` instances for arrows with any cell in the current camera rect. A `Graphics.DrawMeshInstanced`-based fallback for arrows outside the GameObject budget is designed but deferred until profiling shows it's needed (200×200 should be handled by viewport culling alone).

### Menu & navigation

- **Main menu restructures** from the current flat Play / Continue / Settings into a nested tree:
  - **Play**
    - **Singleplayer** → **New** (size select) / **Continue** (resume saved game)
    - **Multiplayer** → **Co-op**
  - **Settings**
- **Co-op hub scene** has: **Host** (create new lobby form with name input), **Join** (code input with validation), and **My lobbies** (the player's registered lobby list).
- **"My lobbies" list**: rich rows showing `[status-dot] [name] [creator display name] [size] [code] [your clear count / total arrows] [last activity]`. Filters: Active / Completed. Sort: Recent activity (default) / Alphabetical. Search by name. Infinite scroll at 20 rows per page.

### Replay & completion

- **Last arrow cleared** triggers a results screen for all connected players (shared timestamp, same experience as solo victory). The lobby transitions to read-only; further clear attempts are rejected with a `lobby_complete` reason.
- **Unified lobby replay**: the existing `ReplayEvent` format is extended with an optional `playerId` field (nullable for solo, required for co-op). A new `player_joined` event type is added. Co-op replays carry a `roster` section in the header listing registered players with display names and colors at completion time.
- **Replay player** (existing `ReplayViewController`) renders remote events in the clearer's player color; the live sidebar is reconstructed over replay time as a timeline of counts and join/leave events.

### Deletion cleanup

- **Soft-delete**: owner deletion marks the lobby `deleted_at` and force-disconnects all connected sessions with a toast ("Lobby deleted by owner"). Registrations remain in the DB for audit but are hidden from "My lobbies". The join code becomes unreachable. Snapshots and event logs are kept under the same retention policy as completed lobbies (strip snapshot after 30 days). Clear counts from a deleted incomplete lobby do not appear on any results screen — the lobby never completed.

## Architecture

### Domain layer additions

- **`Lobby`** (domain, new) — identity (`Id`, `Code`, `Name`, `OwnerUserId`, `CreatedAt`), board parameters (`Width`, `Height`, `Seed`), status (`Generating` / `Active` / `Completed` / `GenerationFailed` / `Deleted`), and a reference to the current `Board`.
- **`LobbyPlayer`** (domain, new) — per-lobby per-player state: `UserId`, `DisplayNameAtJoin`, `ColorAtJoin`, `ClearCount`, `AccumulatedMillis`, `TimerStartedAt`, `LastActivityAt`, `JoinedAt`, `FirstClearAt`, `FinishedAt`.
- **`CoopEvent`** (domain, new) — a single persisted lobby event: `Seq`, `Type` (`player_joined` / `clear_accepted` / `clear_rejected_dep` / `lobby_completed`), `PlayerId`, `ArrowId` (nullable), `TapX` / `TapY` (nullable), `TimestampUtc`, `Metadata` (JSON for forward-compat).
- **`ReplayEvent`** (domain, modified) — add optional `PlayerId` field. Add `player_joined` event type. Bump replay format version to **v3**. v2 solo replays stay readable; v3 replays carry a `roster` array in the header.

### Server

- **New folder `server/ArrowThing.Server/Coop/`** with:
  - **`LobbyService`** — create / get / list-mine / soft-delete, registration, snapshot stripping background job.
  - **`LobbyGenerationWorker`** — the `BackgroundService` running the concurrent generation queue. Honors the per-account and global concurrency caps. Emits progress via an internal event → WebSocket hub.
  - **`CoopHub`** — WebSocket hub. Authenticates via JWT in query string, routes messages by type, owns the per-lobby in-memory session state, enforces rate limits, performs atomic clear validation.
  - **`CoopDtos`** — DTOs for all REST and WebSocket payloads.
- **EF Core additions** (new DB context sets, one migration per phase where schema changes):
  - `Lobbies`, `LobbyRegistrations`, `LobbyEvents`, `LobbySnapshots`, `CoopColors` (the last one a column on `Users`, not a separate table).
- **Program.cs** wires the new endpoints, the hub, the generation worker, and rate-limit middleware scoped to the WebSocket path.
- **Existing code impact**: `User` model gains a `CoopColor` column. `AuthService` gains a `PATCH /api/auth/me` handler for `CoopColor`.

### Client (Unity)

- **New scenes**:
  - **`Coop Hub`** — new scene with `CoopHubController : NavigableScene`. Host form, join form, my lobbies list.
  - **`Coop Game`** — new scene with `CoopGameController : NavigableScene`. Parallels `GameController` but drives an `ICoopSession` instead of local `InputHandler` logic.
- **New view scripts**:
  - **`CoopHubController`** — drives the hub scene.
  - **`CoopGameController`** — scene entry point for an active co-op session.
  - **`CoopClient`** — WebSocket client. Wraps `System.Net.WebSockets.ClientWebSocket` (desktop) / `UnityWebRequest` WebSocket (WebGL fallback). Reconnect with backoff. JSON envelope codec.
  - **`CoopSession`** — stateful session object. Owns the `Board`, the roster, the local player's timer/stats. Raises C# events on remote clears, roster updates, etc.
  - **`CoopSidebar`** — UI Toolkit sidebar element, data-bound to `CoopSession` roster.
  - **`CoopColorPicker`** — a UI component (free HSV picker) embedded in the Account tab of `SettingsController`.
- **Modified view scripts**:
  - **`MainMenuController`** — nested menu state machine (Singleplayer / Multiplayer sub-screens).
  - **`BoardView`** — add viewport culling path (see Phase 2).
  - **`ArrowView`** — accept a per-clear tint color for the brief flash on remote clears.
  - **`TapIndicator`** / **`TapIndicatorPool`** — accept a tint color so remote tap indicators can render in the clearer's color.
  - **`ReplayViewController`** — handle v3 replays with per-player attribution and the roster sidebar.
  - **`ApiClient`** — new endpoints for lobby CRUD and the `CoopColor` field on `PATCH /api/auth/me`.
  - **`SettingsController`** — embed `CoopColorPicker` in the Account tab.

## Protocol Sketches

### REST endpoints

```
POST   /api/lobbies                    [auth]   { name }                → { id, code, status: "generating", shareUrl }
GET    /api/lobbies/me                 [auth]   ?filter=active|completed&sort=...&page=... → { entries: [...], nextCursor }
GET    /api/lobbies/{code}             [auth]                           → { id, code, name, size, status, createdAt, owner, playerCount, yourClearCount, yourRank }
DELETE /api/lobbies/{id}               [auth, owner-only]               → { message }
POST   /api/lobbies/{id}/retry-gen     [auth, owner-only]               → { message }   (only valid on generation_failed)
GET    /api/lobbies/{code}/replay      [auth, registered-only]          → v3 replay JSON  (completed lobbies only)
PATCH  /api/auth/me                    [auth]   { coopColor: "#RRGGBB" } → { coopColor }  (extends existing endpoint)
```

### WebSocket endpoint

```
WS     /ws/coop/{code}                 [auth via JWT query param]
```

One connection per player per lobby. Connecting implicitly registers the player if not already registered (server checks auth and the per-account cap).

### WebSocket message envelope

All messages use the same shape:

```json
{ "type": "string", "seq": 42, "payload": { ... } }
```

- `type` is the discriminator.
- `seq` is a monotonic integer. Server assigns `seq` for server-origin messages (globally ordered within a lobby session). Clients assign their own `seq` for client-origin messages (per-client monotonic); the server uses it to correlate responses to specific attempts.
- `payload` is type-specific.

### WebSocket message types

**Client → Server:**

| Type            | Payload                                                | Notes |
|-----------------|--------------------------------------------------------|-------|
| `hello`         | `{}`                                                   | Opening handshake after auth. Triggers `snapshot` + `roster` + `welcome`. |
| `clear_attempt` | `{ tapX: float, tapY: float, clientTsUtc: string }`    | Tap attempt. Server derives cell via `BoardCoords` floor. |
| `heartbeat`     | `{ focused: bool, lastInputTsUtc: string }`            | Every 15 s. Drives AFK pause state. |
| `goodbye`       | `{}`                                                   | Graceful disconnect. Bypasses 60 s grace. |

**Server → Client:**

| Type               | Payload                                                                                                    | Recipients |
|--------------------|------------------------------------------------------------------------------------------------------------|------------|
| `welcome`          | `{ lobbyId, code, name, size, status, yourPlayerId, yourColor }`                                           | requester |
| `gen_progress`     | `{ pct: float, etaSeconds: int, message: string }`                                                         | all (only during `Generating`) |
| `gen_complete`     | `{}`                                                                                                       | all |
| `lobby_failed`     | `{ reason: string }`                                                                                        | all |
| `snapshot`         | `{ version: int, sizeBytes: int, followingBinaryFrame: true }` — followed immediately by one binary frame   | requester |
| `roster`           | `{ players: [{ id, displayName, color, clearCount, timerMillis, online, finishedAt? }] }`                   | all on change |
| `cleared`          | `{ playerId, arrowId, tapX, tapY, tsUtc, seq }`                                                             | all |
| `rejected_dep`     | `{ playerId, arrowId, tapX, tapY, tsUtc, seq }`                                                             | all |
| `rejected_race`    | `{ clientSeq, reason: "race_lost" \| "lobby_complete" \| "not_your_turn" }`                                 | attempter only |
| `rejected_rate`    | `{ clientSeq, retryAfterMs: int }`                                                                          | attempter only |
| `player_joined`    | `{ playerId, displayName, color, tsUtc }`                                                                   | all |
| `player_online`    | `{ playerId, online: bool }`                                                                                | all |
| `lobby_completed`  | `{ finalRoster: [...], completedAtUtc: string }`                                                             | all |
| `disconnect`       | `{ reason: string }`                                                                                        | requester (before close) |

### Binary snapshot format (v1)

Fixed little-endian. Delivered as a single binary WebSocket frame immediately following a JSON `snapshot` envelope.

```
Offset  Field               Type     Notes
 0      magic               u32      'ATSB' (0x42535441) "Arrow Thing Snapshot Binary"
 4      version             u16      1
 6      flags               u16      bit 0 = has_roster, bit 1 = gzipped_payload
 8      width               u16
10      height              u16
12      seed                i32
16      maxArrowLength      u16
18      reserved            u16
20      arrowCount          u32
24      eventSeqHigh        u32      last applied server seq (for reconnect catch-up)
28      <arrow table>       ...      arrowCount entries, each:
                                       u8   length (in cells, 2..maxArrowLength)
                                       u16  headX
                                       u16  headY
                                       bit-packed direction stream: 2 bits per segment
                                         (0=Up, 1=Right, 2=Down, 3=Left), (length-1) segments,
                                         byte-padded to the next byte
 ...    <roster> (if flag)  ...      u16 count, then per player:
                                       guid playerId, u8 displayNameLen, utf8 name,
                                       u24 color (RGB), u32 clearCount, u32 timerMillis,
                                       u8 online (0/1), i64 finishedAtTicks (0 if n/a)
```

A gzipped variant (`flags bit 1`) gzip-compresses everything after the fixed 32-byte header.

Rough size at 200×200 with ~20 000 arrows: `32 + 20 000 × (1 + 2 + 2 + ceil((L-1) × 2 / 8))` ≈ 150–200 KB uncompressed, ~60–90 KB gzipped. Comfortable for a one-shot transfer on the opening WebSocket frame.

### DB schema (sketch — PostgreSQL, EF Core)

```
Lobbies
  Id                  uuid PK
  Code                text UNIQUE, length 6, uppercase alphanumeric
  Name                text NOT NULL, 1..40 chars (app-validated)
  OwnerUserId         uuid FK Users(Id)
  Width               int
  Height              int
  Seed                bigint
  MaxArrowLength      int
  Status              smallint     (0=Generating, 1=Active, 2=Completed, 3=GenerationFailed, 4=Deleted)
  CreatedAt           timestamptz
  GeneratedAt         timestamptz NULLABLE
  CompletedAt         timestamptz NULLABLE
  DeletedAt           timestamptz NULLABLE
  LastActivityAt      timestamptz  (for idle TTL)
  SnapshotStrippedAt  timestamptz NULLABLE

LobbySnapshots
  LobbyId             uuid PK FK Lobbies(Id)
  Format              smallint     (0=BinaryV1)
  Data                bytea        (gzipped binary snapshot blob)
  UpdatedAt           timestamptz

LobbyRegistrations
  LobbyId             uuid FK Lobbies(Id)
  UserId              uuid FK Users(Id)
  DisplayNameAtJoin   text
  ColorAtJoin         text         (hex #RRGGBB)
  ClearCount          int          DEFAULT 0
  AccumulatedMillis   bigint       DEFAULT 0
  JoinedAt            timestamptz
  FirstClearAt        timestamptz NULLABLE
  FinishedAt          timestamptz NULLABLE
  LastActivityAt      timestamptz
  PRIMARY KEY (LobbyId, UserId)

LobbyEvents
  LobbyId             uuid FK Lobbies(Id)
  Seq                 bigint       (monotonic per lobby)
  Type                smallint     (0=PlayerJoined, 1=ClearAccepted, 2=ClearRejectedDep, 3=LobbyCompleted)
  UserId              uuid NULLABLE
  ArrowIndex          int NULLABLE   (index into the snapshot arrow table)
  TapX                real NULLABLE
  TapY                real NULLABLE
  CreatedAt           timestamptz
  PRIMARY KEY (LobbyId, Seq)

Users  (existing, new column)
  CoopColor           text NULLABLE   (hex #RRGGBB; NULL means "not yet picked, auto-assign")
```

Indices: `Lobbies(Code)` unique, `Lobbies(OwnerUserId, Status)`, `Lobbies(LastActivityAt)` (for TTL sweeper), `LobbyRegistrations(UserId, LobbyId)`, `LobbyEvents(LobbyId, Seq)` PK serves queries.

## Phased Rollout

Each phase is a self-contained PR. Phases are intentionally ordered so every merged phase leaves the build green and the game playable (solo at minimum). Phases 0–4 are "plumbing" and ship no user-visible co-op gameplay; phases 5–8 progressively expose the feature.

### Phase 0 — Unicode font coverage

**Goal.** Replace Unity's default UI Toolkit font with a broad-coverage Noto Sans stack so Unicode display names and lobby names render correctly. Fixes a pre-existing tofu bug for non-Latin display names.

**Work.**
- Download and commit under `Assets/Fonts/`: Noto Sans (Regular + Bold), Noto Sans CJK JP (Regular + Bold), Noto Sans Arabic, Noto Sans Hebrew, Noto Sans Thai, Noto Sans Devanagari, Noto Emoji. All SIL OFL, MIT-compatible.
- Create UI Toolkit Font Assets with **Dynamic** atlas mode. Primary font = Noto Sans; fallback chain in the order above.
- New `Assets/UI/Shared/Fonts.uss` that sets `-unity-font-definition: url("project://database/Assets/Fonts/NotoSans.asset")` on `:root` or the theme stylesheet(s).
- Verify all USS files inherit the font (some may override `-unity-font-style` but not family; confirm no rogue font family overrides exist).
- Update `Assets/UI/Shared/Theme.uss` (or equivalent theme root USS) to reference the new font asset.
- Add an editor screenshot test that renders a label with mixed Latin + CJK + Arabic + Emoji and snapshots it (EditMode test via `UIElements` rendering into a `RenderTexture`).

**Testing.**
- Automated: glyph-coverage test that enumerates a representative set of characters (`山田`, `مرحبا`, `שלום`, `สวัสดี`, `नमस्ते`, `👋🎮`) and asserts the font asset returns a valid glyph index for each.
- Manual: register an account with a Japanese display name; verify leaderboard renders correctly in both themes (dark + light).

**Dependencies.** None. Can merge independently.

**Risk.** Low. Purely additive. If the font asset misconfigures, fallback is Unity's default — same as today.

**Implementation notes.** Font files and glyph coverage tests committed. Font delivery uses `PanelTextSettings` (assigned to all 3 PanelSettings) instead of the originally proposed `Fonts.uss` — this is the standard Unity 6 approach and avoids per-UXML stylesheet includes. No CJK Bold (faux-bold acceptable for display names). No screenshot test — `FontEngine`-based glyph coverage test validates the raw `.ttf` files directly. See `docs/TODO.md` for remaining manual Editor configuration steps.

### Phase 1 — Main menu restructure

**Goal.** Convert the flat main menu into the nested Play → Singleplayer/Multiplayer → ... structure. No co-op functionality yet — the "Co-op" button shows a "Coming soon" stub.

**Work.**
- `Assets/UI/MainMenu/MainMenuScreen.uxml` — add three menu states (`menu-root`, `menu-singleplayer`, `menu-multiplayer`). Each state is a `VisualElement` container; CSS toggles visibility. Keep the existing buttons reachable from the new states.
- `MainMenuController.cs` — introduce a simple state enum (`Root / Singleplayer / Multiplayer`). Update `BuildUI`, `BuildNavGraph` hooks from `NavigableScene` to switch between states without leaving the scene. Back button (Escape / Backspace) pops back one state.
- Multiplayer → Co-op button routes to a stub screen that reads "Co-op: coming soon" with a back button. Removed in Phase 5.
- Update `Assets/Tests/PlayMode/UILayout/MainMenuLayoutTests.cs` to assert the new UI states and their buttons.
- Update `NavigationCoverageTests` to declare the new UI states (`root`, `singleplayer`, `multiplayer`, `coop-stub`) with their expected navigable buttons.

**Testing.**
- PlayMode: layout tests for all new menu states across 5 aspect ratios (part of the existing 21-state matrix — grows to add the new states).
- PlayMode: `NavigationCoverageTests` verifies every visible button is keyboard-reachable in every state.
- EditMode: `SceneNavStackTests` updated — no new scene transitions in this phase, but nested menu states are modeled.
- Manual: navigate with keyboard (Arrow / Enter / Esc), mouse, and touch through every menu path.

**Dependencies.** Phase 0 (so new text rendering uses the new font from the start).

**Risk.** Low. Pure UX change, no gameplay impact.

### Phase 2 — Viewport culling in BoardView

**Goal.** Make `BoardView` render correctly on boards too large for one-GameObject-per-arrow. This unblocks all future large-board work (co-op 200×200, future 1000×1000, and any solo board sizes past the current practical limit).

**Work.**
- Add a `BoardSpatialIndex` helper: a uniform grid keyed by cell, storing the set of arrows with any cell in each bucket. Built once from `Board.Arrows`. Updated on `AddArrow` / `RemoveArrow`.
- Extend `BoardView` with a viewport-culling path:
  - Threshold: `Board.Width * Board.Height >= 3600` (60×60). Below the threshold, behavior is unchanged — all arrows are spawned at `Init` time as today.
  - Above the threshold, `Init` no longer spawns all `ArrowView` instances. Instead, it registers a camera-change listener on `CameraController` that recomputes the visible cell rect on every pan/zoom settle.
  - On visible-rect change, the view diffs visible-arrows-now against visible-arrows-prev and spawns/despawns `ArrowView` instances accordingly, reusing an `ArrowView` pool.
  - `ClearArrowAnimated`, `RemoveArrowView`, and the existing highlight methods all continue to work against the currently-spawned subset. Off-screen clears cause no immediate visual effect; when the camera pans to reveal their former location, the arrow is simply absent.
- Pool size cap: 2000 simultaneous `ArrowView` instances. If the visible rect would require more (zoomed all the way out on a huge board), the rect is downsampled — only every Nth arrow in the visible bucket is spawned as a "preview". This is a graceful degradation, not the primary path; `DrawMeshInstanced` is the future fix (deferred).
- Update `CameraController` to expose a `CameraChanged` C# event that `BoardView` subscribes to.
- Update `BoardGridRenderer` if its tiling quad has cell-count assumptions that break at 200×200 (verify — may already be fine since it renders as a single textured quad).

**Testing.**
- EditMode: new `BoardSpatialIndexTests` covering insertion, removal, and visible-rect queries.
- EditMode: new `BoardViewCullingTests` (may need a lightweight test harness since `BoardView` is currently not easily unit-testable without a camera) — alternative: promote the culling logic into a pure helper class and test that directly.
- PlayMode (Explicit perf): generate and render a 200×200 board, verify `BoardView.Init` completes in under 500 ms and steady-state frame time stays under 16 ms while panning.
- Manual: solo game at existing board sizes (10×10 through 40×40) looks identical to today. A debug-only 200×200 option (behind a `GameSettings` flag, not yet exposed in UI) is playable at reasonable frame rates on a development WebGL build.

**Dependencies.** Phase 0 (no direct dependency, but sequencing avoids font-asset churn during rendering work).

**Risk.** Medium. Touches the core render path used by solo today. Mitigation: the threshold keeps small boards on the unchanged code path, so solo play is unaffected until someone picks a large board.

### Phase 3 — Server lobby CRUD + WebSocket plumbing

**Goal.** Add the server-side lobby model, REST endpoints for create/list/get/delete, and a bare WebSocket hub that authenticates, tracks connected players, and broadcasts test messages. No generation yet, no gameplay.

**Work.**
- New folder `server/ArrowThing.Server/Coop/`:
  - `LobbyService.cs` — create / get / list-mine / soft-delete, registration. Enforces per-account caps (5 active, 50 registered). Generates 6-char join codes with collision retry.
  - `CoopHub.cs` — WebSocket handler. Auth via JWT query param (same helper as the existing API endpoints). Tracks a `ConcurrentDictionary<lobbyCode, LobbyRoom>`; `LobbyRoom` is `ConcurrentDictionary<userId, WebSocket>`. Implements `hello` / `welcome` / `heartbeat` / `goodbye` and a debug `echo` message for wiring verification.
  - `CoopDtos.cs` — DTO records.
- New EF Core migration: `AddCoopLobbies` creating `Lobbies`, `LobbyRegistrations`, `LobbyEvents`, `LobbySnapshots`, and adding `CoopColor` to `Users`.
- `Program.cs` — register `app.UseWebSockets(...)`, map `/ws/coop/{code}`, register `LobbyService` in DI, map new REST endpoints.
- `nginx.conf` (in `server/deploy/nginx/`) — add `proxy_http_version 1.1`, `proxy_set_header Upgrade $http_upgrade`, `proxy_set_header Connection "upgrade"`, `proxy_read_timeout` bump for the `/ws/coop/` location.
- `ApiClient.cs` (Unity) — add REST methods: `CreateLobbyAsync(name)`, `ListMyLobbiesAsync(filter, sort, page)`, `GetLobbyAsync(code)`, `DeleteLobbyAsync(id)`.
- `CoopClient.cs` (Unity, skeleton) — WebSocket client with connect/disconnect/send/receive, JSON envelope codec, reconnect with exponential backoff. No gameplay logic yet.

**Testing.**
- Server integration (xUnit): create lobby, list mine, get by code, delete, auth required, cap enforcement, duplicate-code collision retry, WebSocket connect + auth + echo + disconnect.
- Unity EditMode: `CoopClient` message serialization round-trip (mock WebSocket).
- Manual: create a lobby via `ApiClient` from the Unity Editor menu (extend `ServerHealthCheck.cs` with a "Create test lobby" menu item). Connect a raw WebSocket client (e.g. `websocat`) to the `/ws/coop/{code}` endpoint with a JWT and observe the echo handshake.

**Dependencies.** Phase 1 (for the nested menu that will host co-op in Phase 5, though this phase doesn't modify it). No code dependency on Phase 2.

**Risk.** Medium. First WebSocket endpoint on the server — nginx config, JWT auth path, and the hub lifecycle all get exercised for the first time. Mitigation: ship with the debug echo handler so the wiring can be verified end-to-end before adding real message types.

### Phase 4 — Background generation worker + binary snapshot

**Goal.** Actually generate boards server-side on a background worker, persist them, and deliver the binary snapshot over WebSocket. Lobbies move through the `Generating → Active` state transition.

**Work.**
- `LobbyGenerationWorker.cs` (server) — `BackgroundService` owning a `Channel<GenerationJob>` queue. Worker loop: dequeue → run `BoardGeneration` (reusing the existing domain code) → write `LobbySnapshot` row → update `Lobbies.Status = Active` → broadcast `gen_complete` to subscribers. Honors the 3-global / 1-per-account concurrency cap via an `AccountConcurrencyLimiter` helper.
- Progress reporting: add an `IGenerationProgress` interface that `BoardGeneration` can optionally call into. In this phase, progress is coarse — tick at each major generation phase (candidate pool build, walking, cycle checks). Fine-grained progress is a later polish item.
- `BinarySnapshot.cs` (new, in `ArrowThing.Domain`) — static encoder/decoder for the format described in "Protocol Sketches → Binary snapshot format". Pure C#, testable without Unity or the server.
- `LobbySnapshotRepository.cs` (server) — load / save / strip. Gzips the binary blob when writing.
- `CoopHub.cs` — on `hello` from a client, look up lobby status:
  - `Generating` → send `gen_progress` (with current pct cached from the worker) and register the client for future `gen_progress` / `gen_complete` broadcasts.
  - `Active` → load the snapshot, send `snapshot` JSON envelope, then send the binary frame.
  - `Completed` / `GenerationFailed` / `Deleted` → send `disconnect` with the reason and close.
- `CoopClient.cs` (Unity) — add binary-frame handling and invoke `BinarySnapshot.Decode` to produce a `Board` + roster.
- `POST /api/lobbies` now enqueues a generation job and returns `{ status: "generating" }` immediately.
- `POST /api/lobbies/{id}/retry-gen` re-enqueues a failed lobby's generation.

**Testing.**
- EditMode (domain): `BinarySnapshotTests` — round-trip various board sizes, including edge cases (all-horizontal arrows, all-vertical, mix). Assert byte-for-byte stability across encode/decode pairs.
- Server integration: create lobby → poll until `Active` → connect WS → receive snapshot → decode and verify arrow count and a few spot cells. Concurrency cap test: create 5 lobbies, verify only 3 generate simultaneously.
- Server integration: force a generation failure (inject a bad seed or a fault in `BoardGeneration` via a test double); verify the lobby transitions to `GenerationFailed` and the retry endpoint works.
- Performance: stress-test generation of a 200×200 board on the server; verify it completes in under 30 s.

**Dependencies.** Phase 3.

**Risk.** Medium-high. The binary snapshot format is a new on-the-wire contract that will need v2 later; version it carefully from the start. Generation performance at 200×200 is currently unmeasured — profile early and optimize if needed.

### Phase 5 — Co-op hub UI + color picker + deep-link URL

**Goal.** Wire up the user-facing lobby hub — create, join, list — and the Account-tab color picker. After this phase, players can create a lobby, see it generate, and receive the snapshot on join. Gameplay is still stubbed (no clear attempts yet).

**Work.**
- New scene `Assets/Scenes/Coop Hub.unity` with `CoopHubController : NavigableScene`.
- New UXML/USS under `Assets/UI/Coop/` for:
  - `CoopHubScreen.uxml` — three-panel layout (Host / Join / My Lobbies tabs).
  - `LobbyListRow.uxml` — the rich row from the design (status dot, name, creator, size, code, progress, last activity).
  - `LobbyGenProgress.uxml` — the progress screen players see while the board generates.
- `CoopHubController.cs` — uses `ApiClient` for REST, wires tab switching, filters/sort/search on the list, and transitions to `Coop Game` scene via `SceneNav.Push` on successful join.
- `CoopColorPicker.cs` (view component) — free HSV color picker embedded in the Account tab of `SettingsController`. Persists via `PATCH /api/auth/me { coopColor }`. Local preview updates instantly; server save is best-effort with a toast on failure.
  - **Accessibility note**: the picker displays a small contrast-ratio indicator against the current theme background and a simulated-colorblind preview swatch (deuteranopia / protanopia / tritanopia). No hard enforcement — players can pick whatever they want — but the indicators nudge toward legible choices.
- Update `MainMenuController` — the Phase 1 "Coming soon" co-op stub is replaced with a `SceneNav.Push("Coop Hub")` transition.
- **Deep-link URL handling**: add a `CoopDeepLink` helper invoked early in the app boot (before the main menu loads). On WebGL, read `window.location.search` via `Application.ExternalEval` or the existing `ExternalLinks` integration; extract `?lobby=XXXXXX`. If present, route directly into the hub → Join → pre-filled code flow after the main menu has initialized (via `GameSettings.PendingLobbyCode`).
- `Assets/Tests/PlayMode/UILayout/` — add `CoopHubLayoutTests` covering the three panels × 5 aspect ratios × a few states (empty list, populated list, create-in-progress, join-error).

**Testing.**
- PlayMode layout tests for the hub.
- `NavigationCoverageTests` declarations for the new scene's states.
- `CSSResolutionTests` verifying the hub's hidden-class resolution.
- Manual: create lobby from client → verify it appears in the list → delete it → verify it disappears. Join via 6-char code. Join via share URL (paste deep-link into browser). Color picker saves and reloads.

**Dependencies.** Phases 1, 3, 4.

**Risk.** Low-medium. UI-heavy work, but the backing server work is done.

### Phase 6 — Core gameplay sync

**Goal.** Actually play co-op. Clear attempts, race resolution, optimistic animation, reconnect with grace window. After this phase, co-op is functionally playable without the sidebar or attribution — it looks and feels like solo, just with other players' clears showing up.

**Work.**
- New scene `Assets/Scenes/Coop Game.unity` with `CoopGameController : NavigableScene`. Orchestrates `CoopClient`, `CoopSession`, `BoardSetupHelper` (reused from solo), `CameraController`, and a co-op-specific `InputHandler` variant.
- `CoopSession.cs` — session state: current `Board`, local `CoopPlayer` record (id, color, clear count, timer, active-ness), roster of other players. Raises `ArrowAttemptFailed` / `RemoteCleared` / `RemoteRejected` C# events that the view layer subscribes to.
- `CoopInputHandler.cs` — modified copy of `InputHandler` that calls `CoopSession.TryClearLocal` instead of `Board.IsClearable` directly. Optimistic flow:
  1. On tap, derive the cell, read the local arrow, play the tap indicator + clear animation immediately (if local state thinks the arrow exists).
  2. Fire a `clear_attempt` via `CoopClient`.
  3. Listen for the response:
     - `cleared` with matching `clientSeq` → award credit, update local stats.
     - `rejected_dep` → rollback the optimistic clear animation (the arrow reappears with a brief shake), award nothing.
     - `rejected_race` → keep the animation (the arrow was cleared by someone else anyway), award nothing.
     - `rejected_rate` → show a "slow down" toast; no animation.
- **Atomic clear validation (server)**: `CoopHub` must process clear attempts serially per lobby to avoid race confusion. Either a `SemaphoreSlim` per `LobbyRoom` or a dedicated per-room message queue (preferable for throughput). Each attempt:
  1. Lock the room.
  2. Re-read current arrow at the tapped cell from the in-memory `Board`.
  3. If present and `Board.IsClearable`, mutate (`Board.RemoveArrow`), persist a `LobbyEvents` row (synchronous DB write), release lock, broadcast `cleared`.
  4. If present but not clearable, release lock, broadcast `rejected_dep`.
  5. If absent, release lock, send private `rejected_race` to the attempter.
- **Remote clears**: on receipt of a remote `cleared`, the client locates the arrow in the local `Board`, plays the standard `BoardView.ClearArrowAnimated`, and updates its local copy of the `Board` state.
- **Auto-reconnect**: `CoopClient` implements the 1s/2s/4s/8s/16s/30s backoff. On each reconnect, the client sends `hello` again; the server responds with a fresh full `snapshot`. Server holds the slot for 60 s after disconnect before releasing.
- **Rate limiting (server)**: token bucket per `(lobbyId, userId)`. 10 tokens capacity, refill at 10 tokens/sec. Excess is dropped silently for the first 5 s; after 5 s of sustained over-rate, the player is disconnected with `rejected_rate` then `disconnect { reason: "rate_limited" }`.
- **Heartbeat / AFK state**: client sends `heartbeat` every 15 s with `focused` and `lastInputTsUtc`. Server tracks per-player `activeAt` = `min(heartbeat.lastInputTsUtc, now)` and marks the player `afk` if more than 60 s stale or `focused: false`. AFK state feeds into the timer calculation in Phase 7.
- **Lobby completion detection**: after any accepted clear, the server checks `Board.Arrows.Count == 0`. If true, transition `Lobbies.Status` to `Completed`, broadcast `lobby_completed`, and set all clients to read-only.

**Testing.**
- Domain: no new tests (all existing `Board` tests cover the clearing logic already).
- Server integration: end-to-end test with two simulated clients:
  - Both connect, both receive snapshot, both tap the same clearable arrow → first wins, second gets `rejected_race`.
  - One taps a not-clearable arrow → both receive `rejected_dep`.
  - One taps above the rate limit → receives drops, then disconnect.
  - Disconnect + reconnect within 60 s → slot preserved, snapshot received again.
  - Disconnect > 60 s → slot released, snapshot received again on rejoin, prior clear count preserved in the registration row.
  - Clear the last arrow → `lobby_completed` broadcast, further attempts rejected.
- Unity EditMode: `CoopSession` state machine tests with a mock `CoopClient`.
- Manual: two browser tabs logged into two different accounts, both registered to the same lobby, both tapping arrows. Observe race resolution and rejection feedback.

**Dependencies.** Phase 5.

**Risk.** High. This is where all the concurrency traps live. Mitigation: extensive server integration tests with scripted race scenarios; per-room serial processing (not per-session); explicit lock acquisition order; never hold the DB transaction open across WebSocket writes.

### Phase 7 — Sidebar + per-player attribution + timer + tap propagation

**Goal.** Make co-op feel like co-op. The live sidebar, per-player colors on tap indicators and clear animations, the AFK-aware timer, and toast notifications for critical events.

**Work.**
- `CoopSidebar.cs` (view component) — UI Toolkit element, data-bound to `CoopSession.Roster`. Rebuilds on roster events. Pinned own row; secondary rows sorted by clear count DESC. Collapsible via a header button. On screen widths under 768 px, collapses into a floating player-count pill in the top-right; tapping opens a full-screen list overlay.
- New UXML/USS `Assets/UI/Coop/CoopSidebar.uxml` / `.uss`. Integrated into `Coop Game` scene UI.
- **Tap indicator propagation**: server-origin `cleared` and `rejected_dep` messages now drive local `TapIndicatorPool.Spawn` calls with the clearer's color. `TapIndicatorPool` extended to accept a tint color parameter.
- **Clear animation tint**: `ArrowView.ClearAnimated` (or whatever the existing method is named) extended with an optional `flashColor` parameter. Default stays the current color; remote clears pass the clearer's color. The flash is brief (~150 ms) before the existing pull-out animation.
- **Per-player color resolution**: when a player joins, `CoopHub` reads their `User.CoopColor` (or auto-assigns from a built-in palette if NULL) and includes it in the `player_joined` broadcast. The client stores the mapping in `CoopSession.Roster`.
- **Timer logic (client-side, authoritative locally)**:
  - `CoopPlayerTimer.cs` — tracks `AccumulatedMillis` for the local player. Starts ticking on the local player's first clear attempt (observed from `CoopInputHandler`). Pauses when the window loses focus or when no input has arrived for 60 s. Resumes on focus + input. Reports every 5 s to the server via a `timer_update` message (added to the envelope spec).
  - Server persists the last reported `AccumulatedMillis` in `LobbyRegistrations` and echoes it in `roster` broadcasts so other players see live updates.
- **Toasts**: new `CoopToastManager` (or reuse the existing toast infra). Triggers on `player_joined`, `gen_complete`, `lobby_completed`, disconnect, rate limit, reconnect failed. Does **not** trigger on individual clears.
- **All settings remain local**. The sidebar, color picker, and tap indicators all read from the local player's settings; no co-op logic reads or writes other players' settings.

**Testing.**
- PlayMode: `CoopGameLayoutTests` covering HUD + sidebar + pill-on-mobile across aspect ratios, with a few roster sizes (1, 3, 10 players).
- `NavigationCoverageTests`: sidebar toggle button is keyboard-reachable.
- Unity EditMode: `CoopPlayerTimerTests` — focus/blur, idle timeout, AFK resume, first-clear start.
- Server integration: verify `timer_update` round-trips and `roster` broadcasts carry correct per-player stats.
- Manual: two clients, verify sidebar rows update live, colors match, AFK indicator appears when one tab loses focus.

**Dependencies.** Phase 6.

**Risk.** Medium. Per-player attribution touches the hot rendering path (tap indicators, clear animation). The timer's AFK detection is easy to get wrong — verify against the spec with EditMode tests before manual testing.

### Phase 8 — Completion, results, replay v3, retention

**Goal.** Close the loop. Results screen on lobby completion, read-only post-completion mode, unified co-op replay support, and the background jobs for snapshot stripping and idle-lobby TTL.

**Work.**
- `CoopResultsScreen.cs` — shown when the local client receives `lobby_completed`. Reuses the solo `VictoryController` layout where feasible, but with a full results table sorted by clear count DESC: rank, color dot, display name, clear count, timer. Own row highlighted. Buttons: **Play Again** (back to hub), **Menu** (main menu), **View Replay** (enter replay scene with the unified co-op replay).
- **Read-only mode**: `CoopGameController` switches to a read-only flag on `lobby_completed`. Further taps play a subtle reject animation with no server call. The HUD is replaced by the results overlay; the sidebar stays visible.
- **Replay format v3**:
  - `ReplayEvent.cs` — add nullable `PlayerId` field. Add `player_joined` type.
  - New header field `Roster: List<ReplayRosterEntry>` with `{ PlayerId, DisplayName, Color }`.
  - Bump format version to 3. `ReplayPlayer` and `ReplayVerifier` updated to handle v2 and v3.
  - `LobbyService` builds a v3 replay from `LobbySnapshots.Data` + `LobbyEvents` on demand when `GET /api/lobbies/{code}/replay` is called (registered players only).
- `ReplayViewController` — handles v3 replays: sidebar reconstructed over replay time (roster state at time T is a deterministic function of events up to seq T); remote tap indicators rendered in the clearer's color; the rank tally is shown as a live overlay.
- **Retention background jobs** (on the server, as hosted services):
  - `SnapshotStripperJob` — runs daily. Finds `Lobbies` where `Status IN (Completed, Deleted)` and `CompletedAt/DeletedAt < now - 30 days` and `SnapshotStrippedAt IS NULL`. Nulls the `LobbySnapshots.Data` blob and stamps `SnapshotStrippedAt`.
  - `IdleLobbyReaperJob` — runs daily. Finds `Lobbies` where `Status = Active` and `LastActivityAt < now - 30 days`. Soft-deletes them (same treatment as owner-delete, including disconnecting any lingering connections and broadcasting `lobby_failed { reason: "idle" }`).
- Update `LeaderboardManager` **only** if the v3 replay format change touches the solo leaderboard replay path. It shouldn't — v2 stays the solo format — but double-check.

**Testing.**
- EditMode: `ReplayV3Tests` — encode/decode round-trip, backward compat (v2 still parses), roster reconstruction at arbitrary seq points.
- EditMode: `SnapshotStripperTests` / `IdleLobbyReaperTests` — schedule trigger logic.
- Server integration: full lobby lifecycle (create → gen → clear to completion → GET replay → verify v3 shape → results screen DTO). Retention jobs tested via time-travel helpers.
- PlayMode: `CoopResultsLayoutTests` across aspect ratios.
- Manual: full two-client flow from lobby create through clear-all-arrows. Click "View Replay" and verify the playback looks right (both players' clears shown with correct colors, sidebar animates over time).

**Dependencies.** Phases 4, 6, 7.

**Risk.** Medium. Replay format change has a backward-compat requirement (v2 solo replays must still play). Retention jobs can be catastrophic if misconfigured — gate them behind a feature flag on first deploy and verify the `WHERE` clause against a prod-like dataset before enabling.

## Open Questions

These are decisions deliberately deferred until the phase that implements them. Each is small enough to be resolved in the phase PR without blocking the roadmap.

1. **Auto-assigned default color palette.** When a player with `CoopColor = NULL` joins a lobby, the server picks a color. Question: fixed palette (cycled by user id hash) or lobby-specific assignment (first-available from a palette of 12)? Resolve in Phase 5 when the color picker lands.
2. **Colorblind-safe warnings on the color picker.** How strict should the contrast indicator be? WCAG AA (4.5:1) or stricter? Resolve in Phase 5.
3. **Heartbeat frequency vs battery.** 15 s heartbeat is fine on desktop but may hurt mobile battery. Consider adaptive frequency (15 s while active, 60 s while AFK) in Phase 6 or 7.
4. **AFK timer persistence.** If a player disconnects during an AFK pause, should the timer resume on reconnect or stay paused until the player makes another input? Resolve in Phase 7.
5. **Snapshot streaming for very large boards.** The binary snapshot format assumes a single frame. For hypothetical 1000×1000 boards the frame would be ~5 MB uncompressed — still OK for a one-shot, but chunked delivery may be better. Defer until after 200×200 v1 ships.
6. **Generation progress granularity.** Coarse per-phase progress in Phase 4 is a placeholder. Fine-grained progress (percent per 1000 candidates, etc.) is a polish item to revisit once generation timing is profiled.
7. **Mobile pinch-zoom default.** Phase 5 picks "fit to board" as the initial zoom on mobile, which produces very small touch targets at 200×200. Revisit in Phase 7 if playtesting shows it's unusable — a zoomed-in default with a minimap overlay is the fallback plan.
8. **Sidebar overflow behavior.** Sidebar sorting is clear count DESC with own row pinned. When there are more players than fit on screen, does the list become scrollable or does it switch to a different layout? Resolve in Phase 7.
9. **Solo save-file impact.** v3 replay format bumps may or may not require migrating existing solo `savegame.json` files. If `ReplayData` is persisted with a version field today, no migration is needed. Verify in Phase 8.
10. **Retention job scheduling.** Daily job cadence is stated but the exact time-of-day window (ideally during low-traffic hours) is deployment-specific. Decide in Phase 8.

## Testing Strategy

Each phase carries its own tests; the aggregate expected surface at feature-complete:

**Domain (NUnit EditMode)**
- `BoardSpatialIndexTests` (Phase 2)
- `BinarySnapshotTests` (Phase 4)
- `ReplayV3Tests` (Phase 8)
- `CoopPlayerTimerTests` (Phase 7)
- `CoopSessionStateMachineTests` (Phase 6)

**Server integration (xUnit, Testcontainers)**
- Lobby CRUD + auth + caps (Phase 3)
- WebSocket connect + echo + auth (Phase 3)
- Background generation + snapshot round-trip (Phase 4)
- Generation failure + retry (Phase 4)
- Two-client race resolution scenarios (Phase 6)
- Rate limit enforcement (Phase 6)
- Reconnect within / beyond grace window (Phase 6)
- Lobby completion detection + read-only transition (Phase 6)
- Replay fetch + v3 shape (Phase 8)
- Retention jobs (Phase 8)

**PlayMode UI layout**
- `MainMenuLayoutTests` (extended, Phase 1)
- `CoopHubLayoutTests` (new, Phase 5)
- `CoopGameLayoutTests` (new, Phase 7)
- `CoopResultsLayoutTests` (new, Phase 8)
- `NavigationCoverageTests` declarations extended per new scene / state
- `CSSResolutionTests` extended per new UXML

**Manual**
- Full lifecycle: font coverage → menu nav → create lobby → share URL → second account joins → both play to completion → results → replay.
- Mobile browser playthrough across the same flow.
- Offline / server-down: game still playable in solo mode; co-op hub shows a reachability error.
- Locked account: cannot create or join lobbies.
- Rate limiting: scripted rapid clicks trigger the disconnect.

## References

- [`docs/TechnicalDesign.md`](TechnicalDesign.md) — single source of truth for the overall architecture. Updated as each phase lands.
- [`docs/OnlineRoadmap.md`](OnlineRoadmap.md) — historical roadmap covering the existing online infrastructure (auth, server, leaderboards, replay). Co-op builds on this foundation.
- [`docs/BoardGeneration.md`](BoardGeneration.md) — generation algorithm documentation. Reused unchanged by co-op.
- [`docs/GDD.md`](GDD.md) — game design document.
- `CLAUDE.md` — repository workflow and conventions, including the feature workflow that governs how per-phase `TODO.md` files are used.
- [SIL Open Font License 1.1](https://openfontlicense.org/) — Noto Sans font family license (Phase 0).
