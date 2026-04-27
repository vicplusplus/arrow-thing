# Run-pattern extraction (classic + endless)

Live design doc for this branch. **Scope: domain-owned game loops.** No
server changes, no leaderboards, no verifier — those are queued for a
separate follow-up PR that builds on the foundation laid here.

## Goal

Extract the per-mode game lifecycle into pure-C# domain classes
(`EndlessRun`, `ClassicRun`) so:

1. The view layer (`EndlessModeController`, classic equivalent in
   `ClassicMode` + `InputHandler`) becomes a thin adapter — forwards
   `Update` deltas, forwards player input, subscribes to events for
   HUD / animation reactions.
2. The future verifier instantiates the SAME `Run` class with the SAME
   tuning, replays recorded events through the SAME `HandleTap` API,
   and compares final state. No bespoke "simulator" parallel
   implementation that can drift from the live code.
3. Tests can drive the run synchronously from EditMode without needing
   PlayMode + Unity lifecycle.

## Current state (foundation, already on branch)

- **Sim-time clock** (`EndlessModeController._simTime`): the endless
  game loop reads sim-time everywhere instead of `Time.time` /
  `Time.deltaTime`. Frozen during pause/alt-tab.
- **Canonical push scheduler**: `_lastPushSimTime + interval` threshold,
  not delta-accumulation. Replay-deterministic.
- **Color RNG**: `PortableRandom` seeded from spawn seed (cosmetic but
  reproducible).
- **Unified `ReplayData` v7**: one schema for all modes. Optional
  `simTime` / `cellX` / `cellY` fields on `ReplayEvent`. New `Topout`
  event type. `mode` discriminator on `ReplayData`. Cell-variant
  recorder methods on `ReplayRecorder`.
- **`EndlessTuning` struct** in domain: every game-loop constant in one
  versioned place. `EndlessTuning.V1` is current. Bump version when
  changing tuning in a replay-affecting way.

## Plan

### 1. `EndlessRun` (Domain)

New class wrapping the entire endless lifecycle. Pure C#. Constructed
with `(Board, spawnSeed, paletteCount, tuning)`. APIs:

- `void Advance(float deltaTime)` — caller (live: `Update().deltaTime`,
  verifier: event-to-event sim time delta) advances the clock; run fires
  any push ticks + pending commits whose thresholds have crossed.
- `EndlessTapKind HandleTap(int cellX, int cellY)` — resolves the tap
  against current board state; returns the actual outcome. Verifier
  compares against the kind stored on the replay event; mismatch flags.
- Events: `PendingSpawned`, `PendingCommitted`, `MeterChanged`,
  `ShortfallChanged`, `ToppedOut`. View subscribes; verifier ignores.
- Read-only state: `ClearCount`, `LongestCombo`, `RunDurationSeconds`,
  `Board`, `PendingArrows`, `Meter`, `ActiveShortfall`.

### 2. Refactor `EndlessModeController` (View)

Becomes a thin adapter:
- AddComponent at runtime, instantiates `EndlessRun` in `Initialize`.
- `Update()`: forwards `Time.deltaTime` to `_run.Advance(dt)`.
- Subscribes to events for view reactions (build pending arrow views,
  rebuild meter UI, update danger tint, run topout sequence).
- `HandleRealArrowCleared` (called from `BoardView.SetArrowRemover`)
  delegates to `_run.HandleTap(cellX, cellY)`.

### 3. `ClassicRun` (Domain)

Same pattern, scoped to classic's loop:
- Constructed with `(Board, ReplayRecorder?, GameTimer?)`.
- `ClearResult HandleTap(int cellX, int cellY, double wallTime)` —
  resolves the tap, mutates board, transitions inspection→solve, records
  events, fires victory event on last clear.
- Events: `ArrowCleared`, `BoardCleared`, `InspectionEnded`.
- Optional `_recorder` / `_timer` so verifier can run without recording.

### 4. Refactor `InputHandler` + `ClassicMode` (View)

`InputHandler.HandleTap` becomes lighter — converts screen pos → cell,
delegates to mode's `HandleTap` (a new `ITappable` hook on `IGameMode`?).

`ClassicMode` constructs `ClassicRun` in setup, subscribes to events,
delegates `OnTapResult` to `_run.HandleTap`.

The recorder + timer + autosave logic moves into `ClassicRun` events
that `ClassicMode` listens to.

### 5. Tests

EditMode tests for both `Run` classes:
- `EndlessRunTests`: deterministic replay (same seed + same tap log →
  same final stats), shortfall→topout edge cases, immediate-mode placement.
- `ClassicRunTests`: replay produces same time, victory fires on last
  arrow, autosave-counter equivalent.

These unblock the future verifier — which is just "instantiate Run +
walk events" — without needing dedicated simulator code.

## Out of scope (next PR)

- Server `EndlessScore` model + endpoints
- Verification worker (uses `EndlessRun` from this PR)
- Endless leaderboard UI
- Classic verifier rewrite to use `ClassicRun` (after this lands and
  bakes; classic's existing verifier keeps working in the meantime)

## Open questions

- **Float determinism between Mono (Unity) and .NET (server)**: classic
  verifier already uses identical `Math.*` primitives in both places.
  The `EndlessRun` uses `Math.Cos` / `Math.Pow` (System.Math, double-
  precision then cast to float). Should match `Mathf.*` since Mathf
  wraps Math, but verify with parity tests in the verifier PR.
- **Coop**: not extracted. Server-authoritative; client doesn't own a
  game loop. `CoopSession` already covers most of what would be a
  `CoopRun`. Skip.
- **Adapter boundary for classic input**: `InputHandler` currently
  reaches all the way through `BoardView.TryClearArrow` to play the
  clear animation BEFORE calling the mode's tap handler. With
  `ClassicRun` owning the board mutation, the animation timing might
  need to invert (run first, animation triggered by event). Investigate.
