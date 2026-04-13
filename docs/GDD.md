# Arrow Thing - Game Design Document

## Metadata
- Working Title: Arrow Thing
- Genre: Minimalist puzzle, speed-clearing, competitive PvP (planned)
- Platform(s): WebGL (primary, deployed via Cloudflare Pages); mobile gameplay works (touch/pinch) but menu UI needs a responsive scaling pass before shipping
- Target Audience: Puzzle players who enjoy speed, pattern recognition, and competitive pressure
- Current Version: v0.7.4
- Status: Active development. Playable at https://arrow-thing.com/
- Last Updated: 2026-04-12

## High Concept
- One-sentence pitch: Clear winding grid-based arrows as fast as possible, then weaponize your speed against opponents by sending garbage.
- Core fantasy for the player: Out-read opponents under pressure by instantly spotting free arrows and maintaining clearing flow.
- Design pillars:
  - Readability first (minimalist visuals, clear board state)
  - Speed and flow over deep puzzle solving
  - Competitive pressure through board disruption (garbage)
  - Deterministic core rules with fair procedural generation

## Core Gameplay Loop
1. Scan board for currently free arrows.
2. Tap/click an arrow to attempt a clear.
3. If clearable, resolve clear animation and update board state.
4. Repeat until board is empty (MVP) or until match end conditions are met (PvP modes).

## Controls
- Mouse: Primary control for MVP desktop build.
- Touch: Gameplay input works (tap, drag-pan, pinch-zoom); menu UI needs responsive scaling pass.
- Keyboard: No gameplay use.
- Controller: Out of scope (not a design consideration).

## Player
- Player goal:
  - MVP: Clear the board as quickly as possible.
  - PvP: Clear faster than opponents while managing incoming/outgoing garbage pressure.
- Movement model: No avatar movement; input is direct board interaction.
- Actions:
  - Select arrow.
  - Valid clear removes arrow after clear animation.
  - Invalid clear attempt plays fail animation and returns to original state.
- Resource systems:
  - MVP: No explicit punishment for misclicks. The only cost is the player's own time spent on the failed attempt.
  - PvP planned: garbage meter and packet exchange.

## Arrows System
- Arrow definition:
  - A winding shape occupying multiple contiguous grid cells with a defined arrowhead direction.
- Spawn/generation:
  - Procedural generation for initial board setup.
  - Procedural generation for garbage arrows (post-MVP).
- Behavior:
  - Logically static on the board.
  - Visually animated during clear/fail interactions.
- Selection resolution:
  - On tap, the arrow begins a "pull out" animation as if a string is being pulled.
  - The entire arrow moves along its exit path (snake-like), not a tail-to-head dissolve.
  - Display representation should support a polyline-based animation path derived from the logical arrow path.
  - If unobstructed, clear completes and arrow is removed.
  - If obstructed, motion advances until obstruction, bumps, flashes red, then retracts.
  - Audio feedback accompanies both success and failure.
- Obstruction rule (authoritative):
  - An arrow is clearable only if the ray extending forward from the arrowhead to the board boundary contains no other arrow body cells.
  - This is a discrete board-state query only; no physics colliders/hitboxes are involved.
- Solvability constraint:
  - Generation should avoid impossible boards.
  - Equivalent framing: the dependency graph between arrows must be acyclic (DAG).

## Board / Playfield
- Board topology:
  - Grid-based rectangular board for MVP and initial competitive modes.
- Board size presets:
  - Small (10×10), Medium (20×20), Large (40×40), XLarge (100×100). Player selects from a grid layout in the mode menu.
  - Custom board sizes via width/height sliders (range 2–400). Selection is remembered when returning from a game.
- Occupancy and collision:
  - Cell occupancy is exclusive per arrow body segment.
  - Obstruction checks use arrowhead ray-to-edge logic.
- Camera:
  - Player can drag/pan and zoom.
  - Static framing is avoided for larger boards to reduce visual overload.

## Game States
- Main menu (Play, Continue when save exists, Settings, Leaderboard, Quit on desktop)
- Solo size select (preset grid Small/Medium/Large/XLarge/Custom)
- In-game (loading overlay with progress bar during generation/restore, HUD with timer + trail toggle + quick reset, leave/save modals)
- Clear/victory screen (personal best detection, randomized message, Play Again / Menu / View Leaderboard, background score submission)
- Leaderboard (local/global toggle, 5 size tabs + All, sort modes, scrollable list with context menu, replay launch)
- Replay viewer (seek, speed control 0.5×–10×, play/pause, clearable highlighting with trail lanes)
- Settings (cross-scene singleton overlay; Account, Gameplay, Keybinds, Data, About sections)
- Planned later:
  - Co-op lobbies (see [`docs/CoopRoadmap.md`](CoopRoadmap.md))
  - PvP match countdown / start
  - Match result screen
  - Optional pause

## Difficulty and Progression
- Core challenge source:
  - Spatial awareness and working memory under time pressure.
- Difficulty knobs:
  - Arrow count
  - Board size
  - Arrow length distribution and variance
  - Layout density
- Generation direction (initial):
  - Arrow lengths sampled from a distribution centered on shorter lengths.
  - Minimum length is 2; no fixed design-level maximum length is required.
  - Practical upper bounds can be set per mode/profile for tuning and performance.
  - Distribution shape and exact parameters are tuning variables.
  - Initial arrow-count baseline is deferred until generator playtesting.
- Mode ideas:
  - Fixed-count challenges (example: 200-arrow board leaderboard category)
  - Additional variants as systems mature
- Ranking metric:
  - Primary metric is completion time.

## PvP Vision (Post-MVP)
- Match concept:
  - Players race to clear their own boards using identical core rules.
- Main mode target:
  - Top-out mode as primary competitive ruleset.
  - Incoming garbage can overflow board capacity and cause defeat.
- Alternative mode:
  - Race-to-empty mode with board expansion is possible but secondary.
- Garbage model direction:
  - Clears build outgoing garbage potential during active chains/combos.
  - Outgoing garbage is grouped into packets.
  - Packet size increases with sustained clearing before combo end.
  - Packets send after a delay window.
  - Defensive clearing reduces/cancels incoming garbage during that window (parry-like interaction).
  - If no legal garbage placement exists for required insertion, that board is topped out.
  - For network determinism, garbage events should carry concrete arrow payloads, not only RNG seeds.
- Placement notes:
  - Candidate optimization: maintain an enumerated set of legal insertion positions and sample from it.
- Multiplayer scale:
  - Design should support more than 2 players.
- Tie policy:
  - Ties are allowed and require no tiebreaker.

## UX and Feedback
- Visual feedback:
  - Strong readable distinction between board states.
  - Clear success animation and clear failure bump/retract animation.
  - Red flash on obstruction bump.
- Audio feedback:
  - Distinct success and failure tap responses.
  - Future PvP warning cues for garbage pressure.
- Feel priorities:
  - Snappy input response.
  - High readability while zooming/panning.
  - Rising tension under top-out pressure.

## Art Direction
- Style keywords:
  - Minimalist, clean, high-contrast, legible.
- Palette direction:
  - Restrained palette with functional highlights for state changes.
- MVP asset scope:
  - Simple geometric rendering.
  - Light, purposeful effects supporting readability.

## Audio Direction
- Music:
  - Minimal and focused (optional in MVP).
- SFX priorities:
  - Tap/select
  - Successful clear resolve
  - Obstructed bump/fail
  - Board clear completion cue

## Technical Notes (Unity)
- Target Unity version: Use current project version.
- Architecture priority:
  - Board state and game rules are decoupled from Unity objects.
  - Core logic should run as pure model + controller/services.
  - Unity layer should be primarily view/input adapter over core domain API.
  - Event-driven flow is preferred for state updates and reactions.
- Rationale:
  - Easier testing and determinism.
  - Cleaner multiplayer/server-authoritative migration path.
  - Easier multi-board rendering with minimal shared mutable state.
- Scene structure:
  - Main menu scene (mode select, settings, account panel).
  - Core gameplay scene.
  - Leaderboard scene.
  - Replay viewer scene.
- Key systems:
  - Board model and occupancy map.
  - Arrow model (cells + head direction).
  - Procedural board generator with solvability guarantees.
  - Input command handling and clear validation.
  - Interaction animation system (polyline pull, bump, retract).
  - Timer UI.
- Determinism and timing:
  - Isolate RNG used by board generation.
  - Seeded generation should be supported for reproducible boards.
  - UI timer updates per frame.
  - Final times are resolved from precise input/event timestamps.
  - Replay system is event-log driven: record board events/inputs and play them back deterministically.
  - Replay file format: JSON.

## Production Scope

### Implemented
  - Main menu (UI Toolkit: Play, Continue, Settings, Leaderboard, Quit on desktop). Solo size select in its own scene.
  - Procedural arrow generation with solvability guarantee. Post-process compaction merges trivial collinear chains for cleaner boards.
  - Cross-platform deterministic generation via `PortableRandom` (xorshift32) — same seed produces identical boards on Unity client and .NET server.
  - Core click/tap clear loop with success/fail animations.
  - Timer UI (inspection countdown + solve timer with input-precision final time).
  - Victory screen (grid fade + victory popup with randomized messages, personal best detection, Play Again / Menu / View Leaderboard, keyboard shortcuts R/L/Escape).
  - Map-coloring arrow tinting (graph coloring for adjacent arrow readability).
  - Board size presets Small (10×10), Medium (20×20), Large (40×40), XLarge (100×100), and custom 2–400.
  - Loading progress bar with three-phase model (generation → compaction → finalization).
  - Save/resume with initial board snapshot (no re-generation on resume). Autosave every 10 clears. Leave-game modal with save/discard options. Cancel-generation confirmation modal.
  - Trajectory highlight toggle for large boards (with optional "keep trail after clear" setting).
  - Incremental board display during generation and restore.
  - Local leaderboards and personal best tracking with GZip-compressed replay storage.
  - Global leaderboards backed by server (per-size and cross-size "All" tab, top-50 + player rank context, refresh button).
  - Replay viewer with seek, speed control (0.5×–10×), clearable highlighting with trail lanes, tap indicators.
  - Full keyboard navigation across every UI screen (arrow keys, Enter, Escape, Tab) with rebindable keybinds and conflict detection.
  - Gameplay shortcuts (R reset, T trail, Space click hovered, S save) and leaderboard shortcuts (1–5 size tabs, F favorites, L global toggle, R refresh).
  - Quick retry button on the in-game HUD (mobile-friendly).
  - CSS variable theming with runtime theme switching (4 themes: Dark, Light, Dark Monochrome, Light Monochrome).
  - Shared UI component library (ConfirmModal, EditableLabel, LabeledField, SnapSlider, CustomDropdown, ExternalLinks, GlobalToast).
  - Settings panel as a cross-scene singleton (Account, Gameplay, Keybinds, Data, About).
  - Account system: email-based auth (register, login, verify, forgot/reset password, change email, change password, display name editing). 6-digit codes entered in-app.
  - ASP.NET Core server (.NET 10 Minimal API) with shared domain code, JWT auth + SecurityStamp, PostgreSQL, Resend email.
  - Server-side replay verification with score integrity safeguards (pre-verification gate, async Redis-queued worker, account flagging for casual cheaters).
  - WebGL deployment via Cloudflare Pages with CD pipeline. Server deployment via Docker to VPS with CD pipeline. Discord release announcements.
  - Observability stack: Serilog → Loki, OpenTelemetry → Prometheus, Grafana dashboards (logs, metrics, audit SQL).
  - Global toast singleton (`DontDestroyOnLoad`) for cross-scene error/info notifications with retry buttons.

### Planned
  - Audio feedback for success / fail / clear / board complete.
  - Co-op boards: persistent shared puzzles with WebSocket sessions, per-player attribution, results screen. See [`docs/CoopRoadmap.md`](CoopRoadmap.md) (designed; phased implementation not started).
  - PvP: real-time garbage mechanics, matchmaking.
  - Mobile menu UI responsive scaling pass.

### Non-goals (current)
  - Controller support.
  - Heavy art polish before gameplay validation.
- Note on endless mode:
  - Not a current explicit target, but may emerge naturally during mode and multiplayer testing.

## Open Questions
- None currently open.

## Resolved Questions
- **Target arrow count**: Do not target a fixed count. Provide a maximum (`board area / min arrow size`) and let generation stop naturally when no valid candidates remain.
- **Length distribution**: Controlling the distribution precisely is not feasible without significant performance cost as the board fills. Accept that arrow lengths become less controllable at high density; this is an acceptable constraint given generation speed requirements.

## Changelog
- 2026-02-25: Created initial GDD skeleton in `docs/GDD.md`.
- 2026-02-25: Added detailed draft based on reference game analysis and PvP-forward vision.
- 2026-02-25: Revised to v0.3 with finalized interaction rules, mobile-first input, ray obstruction logic, camera controls, and decoupled architecture direction.
- 2026-02-25: Revised to v0.4 with concrete generation targets, top-out garbage insertion rule, precise timing/replay direction, and discrete collision-check clarifications.
- 2026-02-25: Revised to v0.5 with JSON replay format, tie-allowed policy, and generator-playtest-driven arrow-count decision.
- 2026-02-28: Revised to v0.6 with updated generation bounds language (minimum-only rule with mode-specific practical caps).
- 2026-03-06: Closed open questions on arrow count and length distribution based on generation rewrite experience.
- 2026-03-16: Updated platform target to WebGL-first for MVP; mobile gameplay works but UI scaling deferred. Updated controls section accordingly.
- 2026-03-16: MVP declared complete. Online roadmap documented in `OnlineRoadmap.md`.
- 2026-03-19: Replaced version-based production scope with implemented/planned lists. Added save/resume, autosave, cancel generation modal, trajectory highlights, incremental board display to implemented list.
- 2026-04-01: Updated implemented list with account system, server deployment, theme system, shared UI components, settings panel extraction, Discord announcements. Updated game states and scene structure to reflect current 4-scene layout. Updated planned list (accounts moved to implemented).
- 2026-04-12: Synced GDD to v0.7.4. Added global leaderboards (server-side replay verification), full keyboard navigation with rebindable keybinds, post-generation compaction, cross-platform deterministic generation (`PortableRandom`), score integrity (pre-verification + async worker + account flagging), observability stack, global toast singleton, quick retry button, and replay viewer enhancements (10× speed, trail lanes) to the implemented list. Promoted co-op (designed only) to the planned list and refreshed game states to reflect the current 6-scene layout (Main Menu, Solo Size Select, Game, Leaderboard, Replay, plus Settings overlay).
