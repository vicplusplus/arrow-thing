# Anti-Cheat & Server Hardening

Design document covering score integrity, infrastructure improvements, and replay fraud detection. Each section maps to a separate PR.

---

## Current State

The server verifies replays by regenerating the board from seed, comparing the snapshot, simulating all clear events, and computing solve time from event timestamps. This catches fabricated replays (impossible clears, wrong snapshot, incomplete solves) but does **not** detect:

- **Superhuman speed** — timestamps can be arbitrarily close (e.g. 10ms between clears) and the server accepts them.
- **Stolen replays** — a player can submit another player's replay (or a slight modification) as their own.
- **Unbounded board sizes** — no cap on `boardWidth`/`boardHeight`, so a malicious client can submit absurdly large boards that consume server CPU/memory during verification.

Replay verification is also synchronous and single-threaded — a large board verification blocks the request thread and delays all other score submissions.

---

## PR 1: Pre-Verification & Account Flagging — COMPLETED

Merged: #81, #82

### Problem

Three classes of invalid submissions can be caught with cheap static checks before expensive board regeneration + simulation:

1. **Superhuman speed** — timestamps can be arbitrarily close and the server accepts them.
2. **Stolen replays** — same seed for the same board size from different users is a near-certain indicator.
3. **Out-of-range board sizes** — the client supports up to 400x400; anything beyond that is either a bug or an attack.

### Design

All pre-verification checks run **synchronously on the request thread**. They're all O(1) or O(n) over event count — no board generation, no simulation. Failures flag the **user account** (not individual scores) and reject with 403.

#### Submission flow

1. Deserialize replay.
2. **User flag check** → if `User.Flagged`, reject 403.
3. **Board size check** → if invalid, flag user and reject 403.
4. **Timing check** → if implausibly fast, flag user and reject 403.
5. **Seed duplicate check** → if match found, flag user and reject 403.
6. Idempotency check (existing score with same gameId).
7. Rate limit check.
8. Full verification (`ReplayVerifier.Verify`).
9. Persist score.

No score record is created for flagged submissions. The user account is permanently locked from future submissions until an admin unflags them.

#### Board size validation

| Limit | Value | Rationale |
|-------|-------|-----------|
| Min dimension | 2 | Minimum playable board |
| Max dimension | 400 | Client slider maximum |

#### Minimum solve time

| Check | Formula | Action |
|-------|---------|--------|
| **Min solve time** | `clearCount * 0.08s` | Flag user, reject 403 |

Where `clearCount` is the number of successful arrow clears only (not rejects, misses, or other events). Skipped when `clearCount <= 5` (very small boards where sub-frame solves are legitimate).

#### Seed duplicate detection

On score submission, check if any other user already has a score with the same `(Seed, BoardWidth, BoardHeight)`. If found, flag the submitting user and reject — no score is created. The existing score (the victim) is left untouched.

### Database changes

**User model:**
- `Flagged` (bool, default false) — account-level flag. Flagged users are excluded from leaderboard queries and blocked from submitting scores.
- `FlagReason` (string, nullable) — reason for the flag (e.g. "duplicate_seed", "Solve time implausibly fast.").

### Admin endpoints

```
GET  /api/admin/flagged-users            → list of flagged users with reasons
POST /api/admin/users/{id}/unflag        → clear the user flag, re-enable submissions
POST /api/admin/scores/{id}/remove       → hard delete a score
```

### Leaderboard integration

- All leaderboard queries filter `!s.User.Flagged` — scores from flagged users are invisible.
- Player entry endpoints (`/me`) include `flagged` and `flagReason` fields.
- Client player panel shows "Account flagged: {reason}" when the user is flagged, with no play button.

Also added `miss` replay event type (taps on empty cells) for observability in manual review. Rendered as red tap indicators in the replay viewer, ignored by verification.

---

## PR 2: Redis Infrastructure

### Problem

Multiple features need Redis (verification queue from PR 3, leaderboard caching). The current in-memory `LeaderboardCache` doesn't survive restarts and won't work if the server ever scales horizontally.

### Design

Add Redis 7 Alpine to the Docker Compose stack. No external exposure — internal Docker network only, same as PostgreSQL.

### Docker Compose addition

```yaml
redis:
  image: redis:7-alpine
  restart: unless-stopped
  expose:
    - "6379"
  volumes:
    - redis-data:/data
  command: redis-server --appendonly yes --maxmemory 256mb --maxmemory-policy allkeys-lru
  healthcheck:
    test: ["CMD", "redis-cli", "ping"]
    interval: 5s
    timeout: 3s
    retries: 5
```

### Server integration

- Add `StackExchange.Redis` NuGet package.
- Connection string via environment variable: `Redis__ConnectionString=redis:6379`.
- `IConnectionMultiplexer` registered as singleton in DI.

### Migrate `LeaderboardCache` to Redis

Replace the in-memory `ConcurrentDictionary` with Redis:

```
leaderboard:{width}x{height} → serialized LeaderboardResponse JSON
leaderboard:all              → serialized LeaderboardResponse JSON
TTL: 5 minutes (or until invalidated by score update)
```

Benefits:
- Survives API container restarts
- Shared across multiple API instances if scaled later
- Bounded memory via `maxmemory-policy`

### Changes

- `docker-compose.yml` (both dev and deploy) — add Redis service
- `ArrowThing.Server.csproj` — add `StackExchange.Redis`
- `Program.cs` — register Redis connection
- `LeaderboardCache.cs` — rewrite to use Redis
- `.env.sample` — add Redis connection string
- Tests: update `TestFactory` to provide Redis (or mock)

---

## PR 3: Verification Worker

### Problem

`ReplayVerifier.Verify()` runs synchronously on the request thread. For large boards (100x100+), verification can take several seconds, blocking the ASP.NET thread pool and delaying all other requests.

### Design

**All verification is async.** The API server never runs `ReplayVerifier.Verify()`. It performs the cheap pre-verification checks from PR 1 (size, timing, seed dedup), enqueues the job, and immediately responds. A dedicated worker process consumes the queue and handles the expensive board regeneration + clear simulation.

### Architecture

```
Client                    API Server                    Worker (separate process)
──────                    ──────────                    ────────────────────────
POST /api/scores  ──►     Pre-verify (size, timing,
                          seed dedup)
                          Enqueue to Redis       ──►    BRPOP verify:queue
                          Return 202 Accepted           Run ReplayVerifier.Verify()
                                                        Persist score to DB
                                                        Write result to Redis

GET /api/scores/          Check Redis for result
  {gameId}/status  ──►    Return result or "pending"
```

The API thread does **zero** board generation. Pre-verification (PR 1) catches obviously invalid submissions before they ever hit the queue.

### Worker process

A standalone .NET console app (`ArrowThing.Worker`) in the server solution. References `ArrowThing.Domain` for `ReplayVerifier` and connects to the same PostgreSQL + Redis instances.

```
server/
├── ArrowThing.Worker/
│   ├── ArrowThing.Worker.csproj    # net10.0, references Domain
│   ├── Program.cs                  # Host builder + worker registration
│   └── VerificationWorker.cs       # Worker loop
```

Worker loop:
1. `BRPOP verify:queue 5` (blocking pop, 5s timeout).
2. Deserialize job payload (userId, replayJson, gameId).
3. Run `ReplayVerifier.Verify()`.
4. If valid, persist score to PostgreSQL (same logic as current `GameService`).
5. Write result to `verify:result:{gameId}` with 1-hour TTL.

### Queue structure

```
List:  verify:queue                → FIFO queue of job payloads (JSON)
Key:   verify:result:{gameId}     → verification result JSON (TTL: 1 hour)
```

Job payload:
```json
{
  "userId": "guid",
  "gameId": "guid",
  "replayJson": "...",
  "enqueuedAt": "2026-04-10T..."
}
```

Result payload:
```json
{
  "status": "verified | rejected",
  "rank": 5,
  "isPersonalBest": true,
  "reason": null
}
```

### Scaling

- Default: 1 worker replica in Docker Compose.
- Scale with `docker compose up -d --scale worker=N`.
- Each worker instance is independent — Redis queue handles distribution via `BRPOP`.
- No shared mutable state between workers (each gets its own DB connection).

### Docker Compose addition

```yaml
worker:
  image: ghcr.io/vicplusplus/arrow-thing-worker:latest
  restart: unless-stopped
  environment:
    - ConnectionStrings__Default=Host=db;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}
    - Redis__ConnectionString=redis:6379
  depends_on:
    db:
      condition: service_healthy
    redis:
      condition: service_healthy
```

### Client changes

- `ApiClient.cs` — handle 202 response, add `GetScoreStatusAsync(gameId)` polling method
- `ScoreSubmitter.cs` — on 202, poll status (e.g. 3 attempts, 2s apart). If still pending, show "Score submitted — verification in progress" and stop. Next time the player views the leaderboard, the score will be there if it verified.

### New endpoints

```
GET /api/scores/{gameId}/status → { status: "pending" | "verified" | "rejected", rank?, isPersonalBest?, reason? }
```

### API submission flow (updated)

`POST /api/scores` now:
1. Deserialize replay.
2. Run pre-verification (PR 1): user flag check, board size, timing, seed dedup.
3. Idempotency check (existing score with same gameId).
4. Rate limit check.
5. Enqueue to Redis.
6. Return 202 `{ gameId, status: "pending" }`.

No inline verification path. All boards go through the worker.

### CI/CD

- Separate Dockerfile for the worker (`Dockerfile.worker`), or a multi-stage Dockerfile with build target selection.
- Worker image pushed to `ghcr.io/vicplusplus/arrow-thing-worker`.
- Deploy workflow updated to pull and restart the worker container alongside the API.

### Changes

- New `ArrowThing.Worker` project
- `GameService.cs` — replace inline verification with enqueue; move score persistence to worker-callable service
- `Program.cs` — add status endpoint, remove synchronous verify path
- Docker Compose — add worker service
- CI/CD — build + deploy worker image
- Client: `ApiClient.cs` + `ScoreSubmitter.cs` — handle 202 + polling

---

## Implementation Order

```
PR 1 (pre-verification)        — ✅ COMPLETED (#81, #82)
PR 2 (Redis infrastructure)    — next, prerequisite for PR 3
PR 3 (verification worker)     — depends on PR 2
```

---

## Out of Scope

- **Statistical outlier detection** (analyzing score distributions over time) — future work, requires more data.
- **WebSocket-based server-witnessed timing** — mentioned in OnlineRoadmap.md as a future path.
- **IP-based rate limiting** — already handled by Cloudflare (60 req/10s per IP) and Nginx (30 req/min API, 5 req/min auth).
- **Client-side obfuscation** — security through obscurity, not worth the maintenance cost.
- **Score-level flagging** — considered and removed. User-level flagging is simpler and more effective: a cheater's entire account is locked, not just individual scores. Admin can unflag users after review.
- **Play session tokens** — considered and rejected. Tokens prove presence at start/end but not during play. Save/resume makes continuous check-ins impractical. Bots that actually play the game are effectively impossible to distinguish from fast humans for this game type. PR 1 (timing + seed dedup) covers the real threats.
