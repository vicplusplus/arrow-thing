# Endless leaderboards + verification

Live design doc. Spans Phase 2 (leaderboard storage + display) and Phase 3
(replay verification). Phase 1 (menu mode tabs + endless mode itself)
shipped in PR #145.

## Goal

Player can complete an endless run, submit it to the server, and see it on
a per-size leaderboard. Server re-runs the entire endless game loop from
the player's recorded input log to confirm the claimed clear count /
longest combo / duration weren't fabricated. Mismatches flag the user via
the same mechanism classic uses.

## End-to-end flow

```
1. Player hits Endless on main menu → Game scene → EndlessMode.Setup
2. EndlessMode initializes EndlessReplayRecorder with seed + size + tunings hash
3. Each player tap (and topout) is recorded with a deterministic "sim time"
   timestamp, NOT Time.time. Push schedule + commit cycle are derivable
   from seed + tap log + size.
4. On topout: build EndlessReplayData payload → POST /api/endless-scores
5. Server: pre-verify (basic shape checks), enqueue async verification
6. Verification worker: replay the run via headless EndlessSimulator (port
   of EndlessModeController loop) → recompute clears/combo/duration →
   compare to claimed values → mark Verified or flag user
7. Leaderboard endpoint serves top-N per size, ordered by clears desc,
   tiebreak duration asc
8. Client leaderboard screen gets a top-level [Classic | Endless] tab,
   endless tab shows per-size sub-tabs (5×5 / 10×10 / 20×20)
```

## Phase 2a — Determinism prep + replay capture (client-only)

Foundational refactor; no server changes. Without this, verification can't
work. Doing it as one focused commit so we can land + manually test the
recorded payload before touching the server.

Changes:

- **Replace `Time.time` / `Time.deltaTime` reads in `EndlessModeController`
  with a `_simTime` field** advanced by `Time.deltaTime` each Update.
  `RunStartTime`, `_runEndTime`, `_lastClearTime`, `commitAt` computations,
  `UpdateDangerTint`'s pulse phase, and `RunDurationSeconds` all read from
  `_simTime` instead of wall-clock.
- **Replace `UnityEngine.Random.Range` in `PickComboColorIndex`** with a
  `PortableRandom` seeded from the run seed. Color picking is cosmetic
  for gameplay but should still be deterministic so a replay renders
  identically (same combo bars / arrow colors).
- **`HandleRealArrowCleared` accepts a sim-time tap timestamp** rather
  than reading `Time.time` itself. EndlessMode wires
  `InputHandler`'s `OnTapResult` to forward the cell + sim time.
  - Means we need a way to plumb sim time into the tap. Either:
    - (a) `EndlessMode.OnTapResult` reads `_endless.SimTime` and pushes it
      into a new `_endless.HandleRealArrowClearedAt(arrow, simTime)` overload.
    - (b) `EndlessModeController` exposes `SimTime` and any caller reads
      it to pass back into the same Handle method.
  - Going with (a) — keeps the controller's API tighter.
- **New `EndlessReplayRecorder` (Domain layer)** in
  `Assets/Scripts/Domain/EndlessReplayRecorder.cs`:
  - Records: `tap(simTime, cellX, cellY, kind)` where kind ∈
    {Cleared, Blocked, Missed} and `topout(simTime)`.
  - Push tick / commit / clear are derivable from seed + tap log + size
    on the verifier; not recorded to keep payload small.
  - Why include Blocked/Missed taps? They affect `_lastClearTime` →
    combo timer reset semantics. Plus future ruleset variants might
    care.
- **New `EndlessReplayData` (Domain)**: seed, boardSize (single int —
  endless is square), tuningsVersion (int — bump on tuning changes),
  events list, claimed final stats {clears, longestCombo, duration}.
  Format version field (`version = 1`).
- **`EndlessMode` constructs the recorder**, hands it to the controller
  during Initialize, builds the payload at topout. For Phase 2a no
  network call — just `Debug.Log` the JSON so we can inspect it.

Test:

- Play a short endless run, check console for the logged payload.
- Verify timestamps are monotonic.
- Verify tap count + types match what was actually clicked.

## Phase 2b — Server schema + storage (no verification yet)

- **New `EndlessScore` model** + EF migration:
  ```
  Id, UserId, GameId (client UUID for idempotency), Seed, BoardSize,
  Clears, LongestCombo, DurationSeconds, ReplayJsonGz (gzip),
  Verified (bool), TuningsVersion, CreatedAt, UpdatedAt
  ```
- **`POST /api/endless-scores`**: accept payload, basic pre-verify
  (shape, monotonic timestamps, sane stat bounds), store as
  `Verified=false`, enqueue for verification (worker is a no-op
  stub initially).
- **`GET /api/endless-scores?size=N&top=50`** for leaderboard query.
- **One PB per (user, size) dedup** like classic. Submission only
  replaces if `Clears > existing.Clears` OR
  (`Clears == existing.Clears` AND `Duration < existing.Duration`).
- Replay version policy mirrors classic's
  (`EndlessReplayVersionPolicy.IsAllowed(version)` etc).

## Phase 2c — Client submission + leaderboard rendering

- **Submit on topout** via `ApiClient.SubmitEndlessScoreAsync(payload)`.
  Result screen shows submission status (Submitted / Pending verification /
  Failed with retry button, mirror of classic's pattern).
- **Leaderboard screen**: add top-level `[Classic | Endless]` tab. Active
  tab swaps the size sub-tabs and the score table.
  - Classic tab: existing 4 size tabs (Small/Medium/Large/XLarge/All).
  - Endless tab: 3 size tabs (5×5, 10×10, 20×20). No "All" — combining
    runs across sizes isn't comparable.
- **Endless score table columns**: rank, player, clears, longest combo,
  duration, verified badge.
- Persist active tab + sub-tab in PlayerPrefs same way the menu does.

## Phase 3 — Verification

- **Port endless game loop to `EndlessSimulator` (Domain layer, headless,
  no Unity dependencies)**:
  - All `Mathf.Cos`/`Mathf.Pow` etc. → `(float)Math.Cos`/`Math.Pow` (or
    extract a `PortableMath` helper). Classic verifier already pinned
    these for cross-platform determinism.
  - Push schedule + commit pipeline + combo size + tier ramp + occupancy
    bonus all run identically to client.
  - Drives a sequence: at sim time T, fires push tick → spawns pending →
    advances sim time to next event (next push or next tap, whichever
    is sooner) → processes tap → repeats until topout or end of tap
    log.
- **Verification worker** pulls jobs from Redis queue (extend the
  existing classic queue with a payload-type discriminator, or add a
  parallel `verify:queue:endless` queue — going with parallel queue,
  simpler).
  - Run simulator, compare `(clears, longestCombo, duration)` to claimed
    values. Allow ±0.05s on duration (sim-time floats can drift).
  - Mismatch → flag user (existing `User.Flagged` mechanism), reject score.
  - Match → set `EndlessScore.Verified = true`, refresh leaderboard cache.
- Worker idempotency lock per `GameId` like classic's `verify:lock:` keys.
- Tests: synthetic replays with mutated stats should flag; clean replays
  should verify.

## Open questions

- **Tuning version**: tuning fields (`comboBasePctOfBoard`,
  `pushIntervalAtStartSeconds`, etc.) live on `EndlessModeController`
  with inline defaults. If we tweak tuning later, old replays won't
  re-verify against the new defaults. Options:
  - (a) Freeze a `tuningsVersion` int in the replay; bump it whenever
    tuning changes; the simulator picks the matching tuning table.
  - (b) Accept that tuning bumps invalidate historical replays; bump
    `EndlessReplayVersionPolicy` and reject old submissions via the
    "please update" path.
  - **Going with (b)** for v1 — same approach classic uses. Simpler.
    Bump endless replay version when tuning changes meaningfully.
- **Float determinism between Mono (Unity) and .NET (server)**: classic
  verifier already deals with this and uses identical math primitives
  in both places. Endless should be fine using the same. If drift shows
  up: pin to a shared `EndlessMath` static in domain.
- **Pause / focus loss**: if the player tabs out mid-run, `Time.deltaTime`
  doesn't advance (Unity pauses Update by default in WebGL). `_simTime`
  follows. Server replay needs the same behavior — it does, since sim
  time is derived from tap timestamps + push schedule, not real time.

## Order of operations

1. **Phase 2a** — determinism + recorder. One commit. Test with
   Debug.Log-emitted payload from a real run.
2. **Phase 2b** — server schema + endpoint stub. Migration. Test by
   posting hand-crafted payload via curl, then GET to retrieve.
3. **Phase 2c** — client submit + leaderboard UI. Real run → submit →
   refresh → see entry.
4. **Phase 3** — verification worker. Headless simulator. Hooked to
   queue. Test with intentional bad payloads (modified clear count) to
   confirm flagging fires.

Stages 1–3 land a working leaderboard with unverified entries. Stage 4
adds the integrity layer. We can ship 1–3 to staging first, validate,
then layer 4.
