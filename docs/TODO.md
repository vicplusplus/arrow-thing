# Endless verification worker + endless leaderboards

Live design doc for this branch. **Scope: server-side endless score
storage + verification, plus client-side leaderboard UI integration.**
Builds on the Run pattern from PR #146.

## Goal

Wire endless mode into the same scoreboard pipeline classic uses:

1. **Server**: persist endless runs, verify them via `EndlessRun` (no
   parallel simulator), serve top-N leaderboards.
2. **Client submit**: on topout, post the recorded replay to the
   server, poll for result, show toast.
3. **Client UI**: add an Endless tab to the leaderboard screen (same
   shape as classic) with its own size presets, share the existing
   local/global toggle, persist that toggle across sessions as a
   player preference.

## Server-side

### `EndlessScore` (new EF model)

Mirrors `Score` shape. Separate table because endless has different
stat fields (clears + combo + duration vs solve time + maxArrowLength).
Shared replay-JSON columns.

Fields:
- `Id` (Guid PK), `UserId` (FK), `GameId` (Guid)
- `BoardWidth`, `BoardHeight`, `TuningsVersion`
- `Seed` (int — replay verifier needs it)
- `Clears` (int, primary leaderboard sort key, descending)
- `LongestCombo` (int, telemetry)
- `DurationSeconds` (double, tiebreak — ascending when clears tie)
- `ReplayJson` (string, legacy text)
- `ReplayJsonGz` (byte[])
- `CreatedAt`, `UpdatedAt`

Unique index on `(UserId, BoardWidth, BoardHeight)` — one PB per user
per config. Leaderboard query index on
`(BoardWidth, BoardHeight, Clears DESC, DurationSeconds ASC)`.

PB rule on submit:
- New score's `Clears > existing.Clears` → replace
- Equal clears + `DurationSeconds < existing.DurationSeconds` → replace
- Otherwise → keep existing, return `isPersonalBest = false`

### Endpoints (parallel to `/api/scores`)

- `POST /api/endless-scores` — submit a replay. Same idempotency
  guard, same rate-limit shape, same async-via-Redis-queue dispatch
  as classic. New Redis queue keys (`verify:queue:endless`).
- `GET /api/endless-leaderboards/{w}x{h}` — top-50 by clears desc, duration asc.
- `GET /api/endless-leaderboards/all` — global (best score per user across all configs, ranked by clears).
- `GET /api/endless-leaderboards/{w}x{h}/me` — player's PB at config.
- `GET /api/endless-leaderboards/all/me` — player's global PB.
- `GET /api/endless-replays/{gameId}` — fetch replay JSON. *Optional in this PR* — add only if endless replay viewer is in scope. (Probably: skip viewer this PR.)

### Verification worker

Extend the existing `VerificationWorker` (single hosted service) to
poll the new queue + dispatch by mode:
- Pop from `verify:queue:endless` (after standard / heavy classic queues).
- Deserialize `ReplayData`, route by `mode`:
  - `Classic` / `Coop` → existing `ReplayVerifier.Verify`.
  - `Endless` → new `EndlessReplayVerifier.Verify`.

`EndlessReplayVerifier` is the ~50-line walker we sketched earlier:
instantiate `EndlessRun` with `EndlessTuning.ForVersion(replay.tuningsVersion)`,
walk events advancing sim time between each, compare per-event result
to `run.HandleTap`'s return, on `topout` event check `run.IsActive`,
finally compare `ClearCount` / `LongestCombo` / `RunDurationSeconds`
to the claimed totals.

On success: upsert into `EndlessScores` with PB rule, write Redis
result key, invalidate per-config endless leaderboard cache.
On failure: flag user (same path as classic), reject result.

### Server tests

- `EndlessScoresTests`: submit happy path + PB replacement (more
  clears beats existing; same clears beats existing with shorter
  time; worse score keeps existing) + idempotent retry.
- `EndlessLeaderboardTests`: top-50 ordering, `/me` endpoint, unauth
  for `/me`.
- `EndlessReplayVerifierTests`: in-process verification of a
  hand-crafted endless replay (kind mismatch flagged, claimed-clear-
  count mismatch flagged, valid replay accepted).

## Client-side

### Submit flow

`EndlessMode.OnToppedOut` already builds a `ReplayData` payload and
logs it. Add `EndlessScoreSubmitter` mirroring classic:
- `Submit(replay)` fire-and-forget.
- Internal: `ApiClient.SubmitEndlessScoreAsync(replayJson)`, poll
  `GetEndlessScoreStatusAsync(gameId)` up to N times, surface result
  via `GlobalToast`.

`ApiClient` gains: `SubmitEndlessScoreAsync`, `GetEndlessScoreStatusAsync`,
`GetEndlessLeaderboardAsync`, `GetEndlessLeaderboardAllAsync`,
`GetEndlessPlayerEntryAsync`, `GetEndlessPlayerEntryAllAsync`.

### Local endless leaderboard

Add a parallel `EndlessLeaderboardEntry` + extend `LeaderboardStore`
(or add `EndlessLeaderboardStore`) for local PBs. Decision: keep it
separate (`EndlessLeaderboardStore`) because the sort key differs
(clears desc, not time asc). Same on-disk format pattern, separate
JSON file.

Captured in `EndlessMode.OnToppedOut` alongside the server submit —
local PB is shown immediately even when offline / pending verification.

### Leaderboard screen UI

Current shape: tabs are board sizes (5/6/10/20/40/100/all). Toggle
between local and global is a single button.

New shape:
- **Top tab row**: Classic | Endless (mode selector).
- **Inner tab row**: changes based on mode.
  - Classic: existing 5/6/10/20/40/100/all sizes.
  - Endless: 10×10 / 20×20 / 40×40 / all (mirroring endless preset picker).
- **Local/global toggle**: shared, applies to both modes. **Persist
  the toggle as `PlayerPrefs` (key: `leaderboard.viewMode`,
  values: `local` / `global`).**
- Reload entries when either tab row or the local/global toggle changes.

### Manual test cases (filled in after implementation)

1. Top out a 10×10 endless run online. Toast announces submission;
   server result returns within polling window. Refresh leaderboard
   → entry appears at correct rank.
2. Top out offline. Toast says submission failed / queued. Local
   leaderboard still shows the run.
3. Submit a run that's a worse PB than existing → server returns
   `isPersonalBest=false`; existing entry stays.
4. Submit better PB (more clears) → existing entry replaced.
5. Submit equal clears with faster time → existing entry replaced.
6. Open leaderboard screen, toggle local↔global, switch to Endless tab.
   Toggle position persists.
7. Quit app, reopen, navigate to leaderboard → toggle is in the same
   state as last session.
8. Switch between classic Small (5×5) and endless 10×10 — content
   doesn't shift / jump (alignment carries over the size-tab work
   from the prior PR).
9. `gh api repos/.../actions` — server tests pass on CI.

## Out of scope

- Endless replay viewer (would need its own playback + meter UI).
- Co-op verifier rewrite (server-authoritative, no client replay to verify).
- Tuning version migration tooling (we don't have V2 yet).
- Classic verifier rewrite onto `ClassicRun` (separate cleanup PR
  after this one bakes).

## Open questions

- **Float determinism between Mono (Unity) and .NET (server)**:
  classic verifier already uses identical `Math.*` primitives in both
  places. `EndlessRun` uses `Math.Cos` / `Math.Pow` / `Math.Round`.
  Add a parity test in `ArrowThing.Server.Tests` that hashes the
  meter trajectory of a fixed-seed run server-side and compares to a
  client-side hash captured during a controlled test run. If they
  drift on any platform, switch to a fixed-point implementation
  before shipping.
- **Endless leaderboard "all" sort**: classic ranks by largest board
  area then fastest. For endless, "best" across configs is ambiguous
  (10×10 with 50 clears vs 40×40 with 30 clears). Proposal: rank by
  clears desc, then duration asc, then board area desc as a final
  tiebreak. Document on the endpoint.
- **Submit visibility**: classic shows a toast and that's it. Endless
  result screen could show "submitted as #N" inline if the server
  returns within the post-topout delay window. Punt to a polish
  follow-up if it complicates the result-screen state machine.
