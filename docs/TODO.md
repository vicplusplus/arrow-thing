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

### 3. `ClassicRun` (Domain) ✅

Done. `Assets/Scripts/Domain/ClassicRun.cs`. Constructed with
`(Board, ReplayRecorder?, GameTimer?, alreadyCleared)`. Two driving
entries that share the same private state-update routine:

- `ClassicTapKind HandleTap(cellX, cellY, wallTime, worldX, worldY)`
  — full path: classifies, mutates board on Cleared, transitions
  timer, records event. Verifier-facing.
- `void RegisterViewTap(kind, cellX, cellY, wallTime, worldX, worldY)`
  — live companion: trusts kind from `BoardView.TryClearArrow` (which
  already mutated the board) and applies only timer + recorder side
  effects. Avoids double-mutation.

Events: `InspectionEnded`, `BoardCleared`. Recorder + timer are
optional so the verifier can instantiate ClassicRun headless.

### 4. Refactor `ClassicMode` ✅

Done. `OnTapResult` now forwards every tap to
`ClassicRun.RegisterViewTap`; the run owns timer transitions, replay
recording, and `BoardCleared` detection. ClassicMode keeps view-only
concerns (autosave threshold, victory wiring via the existing
`BoardView.LastArrowClearing` subscriber, save/leave flow). No
`InputHandler` changes were needed — the existing `TapResult` callback
chain plumbs cell + world pos + wall time end-to-end.

### 5. Tests ✅

- `EndlessRunTests` ✅ — determinism (same seed + tap script → same
  final stats), shortfall→topout, no-op deltas, post-topout duration
  lock.
- `ClassicRunTests` ✅ — out-of-bounds is a no-op, missed taps don't
  end inspection, blocked taps do, first/last clear classification,
  `BoardCleared` event + timer.Finish on the last arrow,
  `RegisterViewTap` parity with `HandleTap`, post-completion no-op,
  null-recorder/timer construction.

These unblock the future verifier — "instantiate Run, walk events,
assert state" — without needing dedicated simulator code.

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
- **Adapter boundary for classic input** ✅ Resolved by the dual-API
  split on `ClassicRun`: `BoardView.TryClearArrow` keeps owning view
  mutation + animation on the live path, and `RegisterViewTap` lets
  the mode notify the run after the fact (no double-mutation).
  Verifier uses `HandleTap` which mutates the board itself. Both
  entries funnel into the same private state-update routine so the
  same kind of tap produces identical timer + recorder side effects
  regardless of entry point.
