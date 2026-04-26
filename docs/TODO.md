# TODO: Singleplayer Endless Mode

## Purpose

Prototype the pressure / readability / difficulty-ramp subsystem that will later underpin PvP, in isolation from opponent mechanics. Validates whether "topping out" can feel intuitive on an arrow board despite no gravity and no fixed death direction.

This is a scoped prototype, not a shipping mode. It exercises the generation and preview infrastructure that PvP will need; the garbage-meter mechanics are designed but only partially exercised here (passive generation skips the meter).

## Design

### Board

- Fixed 20x20 outer bounds.
- Initial fill: ~70% occupancy, targeted at the center. Leaves a 2-3 cell ring of empty cells at each edge as initial "wiggle room."
- Loss condition (topout): no valid arrow candidate can be placed anywhere within the 20x20 bounds. This is the natural exhaustion of the generator — `TryGenerateArrow` returns false for every head candidate. Visually the outer ring being saturated is the obvious cue.

### Warning tint

Tetrio-style escalating danger tint. Triggered by **candidate ring** rather than raw fill %: the Chebyshev distance of the centermost placeable head is the actual "how close to topout" signal, since it accounts for dependency-locked cells that fill % would ignore.

Single tier on a 20x20 board (center at ring 0, edge at ring 10):

- **Danger tint** — centermost available head in the initial wiggle-room band (outer 2-3 rings, i.e. ring ≥ 8). Red overlay, possibly pulsing.

Tint reacts to the generator's next-candidate ring each spawn tick, not to fill %. When the player clears arrows in outer rings and frees candidates closer to center, tint clears. When passive spawn pushes outward into the wiggle-room band, tint activates.

### Generation changes

Current generator (`Board.CreateInitialArrowHeads` + `NativeGeneration.TryGenerateArrow`) samples head candidates uniformly at random from the full candidate pool.

Endless mode generation instead:

1. Sort / select head candidates by **centermost first**: candidate with minimum `max(|x - cx|, |y - cy|)` where `(cx, cy)` is the board center. Chebyshev distance to center — produces square-ring fill pattern.
2. Attempt to build a body of a random target length from that candidate. Reuse `GreedyWalk` and all existing cycle-detection / occupancy / swap-and-pop optimizations.
3. On failure (cycle, dead-end, tail shorter than MinArrowLength), back off and advance to the next candidate by Chebyshev-distance order. After N body attempts for a given head, move on (same back-off rule as current generator).
4. Topout is signaled when the generator exhausts all candidates — no arrow can be placed anywhere in bounds.

The centermost-first policy applies to **both** initial fill and in-game passive generation. That is what produces the square-ring topout feel: pressure radiates outward from the center.

### Garbage lifecycle (design; only partially exercised in singleplayer)

Three phases per garbage unit:

1. **Meter.** When an arrow is cleared in PvP, the garbage meter fills. Two timers start:
   - **Combo timer** — subsequent clears within this window extend the current combo. Each clear **resets** the combo timer (sliding window). The combo stays open as long as clears keep landing inside the window.
   - **Commit timer** — runs once from the first clear that started the meter; does not reset on subsequent clears.

   Any clear made by the opponent **at any point during the meter phase** (i.e. before commit) reduces incoming garbage, with combo bonuses amplifying the reduction. The cancellable window spans the entire meter phase — not only the commit-timer window — because the combo timer can extend past the commit timer and the meter stays pending until both expire.
2. **Preview.** Commit to ghost arrows fires only when **both** timers have expired. If the combo timer is still live (player is still chaining clears) after the commit timer expires, the meter waits — commit is gated on the combo actually ending. Once both have expired, the accumulated combo is generated via the centermost-first generator and rendered as flashing semi-transparent arrows that **do not block clearability rays**. Existing arrows' rays pass through ghosts. A preview timer starts.
3. **Commit.** When the preview timer expires, ghosts become real arrows and begin blocking rays.

Passive generation skips phase 1 entirely: it generates directly into phase 2 (ghost preview) on its own schedule. This gives the singleplayer mode the same readability behavior PvP will have (ghosts telegraph incoming pressure) without needing an opponent.

**Provisional timer values** (tune in playtest):

- Combo timer: **1s** (sliding window, resets on each clear)
- Commit timer: **5s** (single-shot from meter start)
- Preview timer: **3s** (ghost visible duration before commit)

### Passive generation schedule

Two inputs:

- **Clear count (monotonic)** — skill-progression signal. Skilled players reach higher difficulty sooner; time-based would flatten skill expression.
- **Board fill state (transient)** — self-regulating signal. Sparse board should spawn faster (prevent boring empty phase); dense board should spawn slower (prevent unfair pile-on when the player is already near topout).

Effective rate:

```
effectiveRate = baseRate(clearCount) * fillModifier(candidateRing)
```

- `baseRate(clearCount)` — monotonically increasing. Start with step function every K clears; compare against linear and mild quadratic in playtest.
- `fillModifier(candidateRing)` — inverse function of candidate ring distance from center. When centermost available head is near center (sparse board), modifier is > 1 (spawn faster). When candidate ring is in the outer wiggle band (dense board, danger tint active), modifier is < 1 (back off). Floor is nonzero — topout must remain reachable; the modifier slows pressure, never pauses it.
- Spawn picks the current centermost available head via the generator; if topout is reached during a spawn attempt, the run ends.

Specific curves for both functions are TBD — tune in playtest.

### Pending arrow visuals

- Base alpha: **25%**, modulated by sine wave **±10%** (range 15% – 35%).
- Wavelength: **1s**.
- Color / sprite otherwise identical to real arrow so direction is immediately readable.
- Renders via the existing `Assets/Art/Shaders/ArrowBody.shader` — already configured for transparency (`Queue=Transparent`, `Blend SrcAlpha OneMinusSrcAlpha`, `ZWrite Off`, returns `_Color.a`). No material reconfiguration needed; `ArrowView.EnterPendingMode` writes alpha to the existing material instance.

### Run end

Run ends on topout. No restart-in-place. Transition to result screen showing score, longest combo, run duration.

## Open questions

- **Initial fill generator reuse.** Current `FillBoardIncremental` fills to arrow-density exhaustion. Endless mode wants a target occupancy (~70%) and center bias. Decide whether to parameterize `FillBoardIncremental` or add a sibling entry point.
- **Ramp curve shape.** Start with step function (every K clears, rate +=1). Compare against linear and quadratic in playtest.

## Implementation status

Delivered so far:

- **Phase 1** — centermost-first generator ordering + topout-by-exhaustion + full invariant preservation. `NativeGeneration.TryGenerateArrow` gained a `centermostFirst` bool param; `BoardGeneration.FillBoardIncremental` propagates. EditMode tests in `Assets/Tests/EditMode/CentermostGenerationTests.cs`.
- **Phase 2** — domain-layer ghost infrastructure. `PendingArrow`, `EndlessBoardSession`, `NativeGeneration.RemoveArrow` / `PlaceArrowExplicit`. Asymmetric dep-graph participation (cycle-validated but clearability-transparent) validated. EditMode tests in `Assets/Tests/EditMode/EndlessBoardSessionTests.cs`.
- **Phase 3 (code-only)**:
  - `GameMode` enum + `GameSettings.Mode` field.
  - `ArrowView.EnterGhostMode()` / `ExitGhostMode()` — sine-alpha modulation (50% ± 10%, 1s wavelength, shared phase via `Time.time`).
  - `EndlessModeController` MonoBehaviour — session ownership, passive spawn scheduler (clear-count baseRate with candidate-ring fillModifier), preview→commit pipeline, topout detection, scoring counters, event hooks (`ToppedOut`, `GhostSpawned`, `GhostCommitted`).
- **Refactor interlude** — `IGameModeStrategy` + Classic/Coop/Endless concrete strategies. Centralizes the previously-scattered `if (_isCoopMode)` branches in `GameController.Update` / `WireHud` / `WireInput` / `WireVictory` into a single dispatch. Endless integration plugs into the strategy slot rather than scattering a third branch. Stage B/C decomposition plan kept on aux branch `refactor/gamecontroller-decomposition-plan`.
- **Phase 4a (integration backbone)**:
  - `BoardGeneration.FillBoardIncremental` gained `targetCellCount` param — endless mode stops at ~70% occupancy; classic stays at saturation.
  - `BoardView.SetArrowRemover(Action<Arrow>)` — endless mode injects `EndlessBoardSession.ClearRealArrow` so the long-lived `NativeGenerationState` stays in sync with played-board removals.
  - `EndlessModeStrategy` fleshed out: instantiates `EndlessModeController`, routes clears, hides retry button, wires topout to scene-pop (placeholder until 4b result screen).
  - `GameController.GenerateBoard` branches on `GameSettings.Mode`: endless uses `centermostFirst=true` + `targetCellCount = w*h*0.7`.
  - MainMenu UXML: `endless-btn` next to start button. Wired to `OnStartEndless` which sets `GameSettings.Mode = GameMode.Endless` before pushing the Game scene.
  - Defensive: `OnStartGame` and `VictoryController.OnPlayAgain` explicitly pin `Mode = Classic`; `GameSettings.ResumeFromSave` resets `Mode` (saves are classic-only in phase 4).

- **Phase 4b (UI polish)**:
  - **Danger tint** — `endless-danger-overlay` element added to `Assets/UI/Game/GameHud.uxml` (full-screen, picking-mode ignore). USS class `.endless-danger--active` toggled by `EndlessModeController.UpdateDangerTint` based on `EndlessBoardSession.CentermostCandidateRing()` vs `dangerRingThreshold`. CSS `transition` handles fade in/out.
  - **Result screen** — `EndlessResultController` (in `Assets/Scripts/View/HUD/`) builds endless-specific overlay programmatically inside the existing `victory-overlay` container, reusing `victory-box` / `victory-message` / `victory-time` / `victory-btn` styles. Displays clears, longest combo, duration; play-again preserves `Mode = Endless`; menu pops scene. Wired from `EndlessModeStrategy.OnToppedOut`.
  - **Combo timer break** — self-driven in `EndlessModeController.Update`. Resets combo when `Time.time - _lastClearTime > comboTimerSeconds` (default 1s). `BreakCombo()` still exposed for external triggers.

Deferred to later milestones:

- **Full per-mode encapsulation** (Phase 4d). Currently only endless owns its UXML asset (`EndlessHud.uxml`), injected via `EndlessModeStrategy`. Classic and co-op still share `GameHud.uxml` and `VictoryPopup.uxml`. Goal: every mode owns its own HUD UXML, result UXML, win condition, and generation strategy as a self-contained module so changes to one can't accidentally break another.
  - Promote `IGameModeStrategy` to a richer `IGameMode` interface (HUD asset, result asset, generation params, win-condition hook, lifecycle).
  - Each mode becomes a sibling MonoBehaviour on the GameController GameObject with its own SerializeFields (UXML assets, mode-specific tunables).
  - GameController instantiates the active mode based on `GameSettings.Mode` and delegates: clear callbacks, victory wiring, end-of-run handling.
  - `ClassicMode` extracts: timer-label ownership, victory wiring, `WireVictoryDefault`, autosave routing, replay save.
  - `CoopMode` extracts: WebSocket lifecycle, reconnect, roster, sidebar, completion handling — supersedes the existing `CoopModeStrategy`.
  - Aligns with stage C of `refactor/gamecontroller-decomposition-plan` aux branch — those extracts are the same refactor, just framed as "decomposition" rather than "modes."

- **Endless leaderboard / replay** — out of phase 4 scope; stage C1 of the GameController decomposition (see `refactor/gamecontroller-decomposition-plan` branch) would naturally absorb endless save/replay support.

- **Buffer-zone generation** — late-game endless gets repetitive: with the board mostly full, the only remaining candidates are at the outer ring, and there are very few of them, so the same arrows respawn in the same spots immediately after being cleared.
  - Proposal: expand the generator's grid by a buffer of M cells (e.g. 3) around the visible w×h board. Arrow heads can sit in the buffer; arrow bodies can extend into the buffer; but every arrow must have at least one cell in the main visible area.
  - Topout becomes "no arrow can be placed even partially in the main board" instead of "candidate pool exhausted."
  - Implementation cost: expand `NativeGenerationState`'s occupancy/ray-index/candidate-pool to (w+2M)×(h+2M); add an "intersects main area" predicate during candidate selection and topout check; rendering must handle arrows whose cells extend beyond the visible board (either render them clipped, or render the buffer too at reduced opacity).
  - Significant change touching generation + view layers; sized for its own session.

## Implementation plan

1. **Generator extension.** Add centermost-first candidate ordering as a mode flag on the generator (or a new entry point). Keep existing uniform-random behavior as the default; endless mode opts in. Unit tests in `Assets/Tests/EditMode/` verify ring-fill order and that topout = no-candidate-placeable.
2. **Endless mode initial fill.** Either parameterize `FillBoardIncremental` with a target-occupancy stop condition or add `FillBoardEndless`. Pick whichever is the smaller change.
3. **Ghost arrow data model.** Domain-side representation of a "pending" arrow (cells + direction + commit-at-tick). Asymmetric participation in the dependency graph:
   - **Cycle validation: yes.** Ghost spawn must run full cycle detection against the *union* of real arrows + already-pending ghosts, as if the ghost (and all earlier pending ghosts) were committed. A candidate that would produce a cycle upon commit is rejected at spawn time, not at commit time — we don't want to commit a ghost that turns out to form a loop. The generator's existing `ComputeReachableSetEarlyAbort` must treat pending ghosts as real for this check.
   - **Clearability blocking: no.** Real arrows' `dependsOn` sets do not contain ghost arrows. `IsClearable` on a real arrow ignores ghosts entirely. Rays visually/logically pass through ghosts until commit.
   - **At commit:** ghost's precomputed forward and reverse deps flip from dormant to live. Real arrows whose rays cross committed cells now have those ghost-turned-real arrows in their `dependsOn` sets.

   Storage: extend `Board` with a parallel pending-ghost collection, or sibling `PendingArrows` component — TBD during implementation. Must expose a "would-be-committed graph" view to the generator for spawn-time cycle checks.
4. **Ghost rendering.** View-side `GhostArrowView` (or similar) with semi-transparent material. Base alpha 50%, sine-modulated ±10% at 1s wavelength. Does not interact with input.
5. **Spawn scheduler.** Clear-count-driven tick that pushes new arrows into the ghost pipeline. Lives in a new `EndlessModeController` (view layer). Per-spawn arrows call `NativeGeneration.TryGenerateArrow` directly against the live `NativeGenerationState` — **not** through `FillBoardIncremental`. Compaction is an initial-fill-only secondary pass (merges trivial chains en masse); running it per spawn would be pointless work and would change post-commit arrow identity. Initial endless-board fill is the only place that runs compaction.
6. **Topout detection.** When the generator returns "no candidate placeable," end the run; transition to a result screen.
7. **Scoring.** Track clears, longest combo, run duration. Display during run and on result screen.
8. **Singleplayer UI entry point.** Add an "Endless" option to the singleplayer mode selection UI so the mode is reachable. Reuse existing singleplayer menu layout; add a new button that routes to `EndlessModeController` instead of the standard fixed-board flow.

Garbage meter (phase 1 of the lifecycle) is **not** implemented in this prototype — passive generation skips straight to phase 2. Meter is deferred to the PvP milestone.

### Generation note: clears do not invalidate generator state

When spawning a ghost after a clear, the generator does **not** need to recompute dependency relationships for already-committed real arrows. Clearing an arrow can only *remove* edges from the dependency graph (via `RemoveArrow`) — it never creates new dependencies, because removing an arrow from a ray can only unblock, never block. The `_dependsOn` / `_dependedOnBy` / spatial ray index / `_depsBitsFlat` state is strictly monotonically shrinking between spawns.

Practical consequence: the generator's cycle-detection caches for the *existing* arrows stay valid across clears. Only the new candidate's forward deps and reachability set need fresh computation (as today). No rebuild of per-arrow dep bitsets on clear.

## Testing

### Automated (domain)

- Centermost-first candidate ordering: given a partially filled board, the next generated arrow's head is at minimum Chebyshev distance among valid candidates.
- Topout condition: construct a board where no valid arrow can be placed (near-full, all edges dependency-locked) and verify `TryGenerateArrow` returns false for all candidates.
- Centermost-first generator preserves DAG property (no cycles in output).
- Centermost-first generator respects min arrow length and occupancy rules (existing invariants).
- Initial-fill targeting ~70% occupancy produces occupancy in a tolerance band (e.g. 65-75%).

### Manual (user-facing)

Record pass/fail before marking the feature complete.

- [ ] Initial board fills with visible center-out bias; outer ring is mostly empty.
- [ ] Clearing arrows eventually triggers passive spawn; spawn cadence noticeably accelerates after many clears.
- [ ] Ghost arrows are visibly distinct from real arrows; direction is readable.
- [ ] Ghost arrows do not block real arrows' clearability (cleared an arrow whose ray passed through a ghost — succeeds).
- [ ] Commit transition is smooth; arrow becomes blocking at the moment it becomes visually solid.
- [ ] Topout is reached when board is saturated to the outer ring; run ends cleanly to result screen.
- [ ] Score / longest combo / run duration display correctly on result screen.
- [ ] At high spawn rate, the board stays in the "hard middle" density range — no extended easy-tail phase.
- [ ] Endless mode is reachable from the singleplayer menu.
- [ ] Danger tint activates when candidate ring enters outer wiggle band; recedes when inner candidates freed by clears.

## Cleanup

- Delete this `TODO.md` before the PR is merge-ready.
- Update `docs/TechnicalDesign.md` to reflect the new endless mode components and the ghost arrow / preview infrastructure.
- Update `docs/BoardGeneration.md` if centermost-first ordering is added as a first-class generator mode (not just a one-off).
- Reflect any new UXML/USS in PlayMode layout tests under `Assets/Tests/PlayMode/UILayout/`.
