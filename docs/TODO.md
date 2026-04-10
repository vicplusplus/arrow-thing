# PR 3: Verification Worker

Reference: `docs/AntiCheatDesign.md` § PR 3

## Plan

1. **Create `ArrowThing.Worker` project** — .NET 10 console app with `Microsoft.Extensions.Hosting`. References `ArrowThing.Domain` (for `ReplayVerifier`). Uses `StackExchange.Redis` + `Npgsql.EntityFrameworkCore.PostgreSQL`. Add to solution.

2. **`VerificationWorker`** — `BackgroundService` that BRPOP from `verify:queue`, runs `ReplayVerifier.Verify()`, persists score to DB, writes result to `verify:result:{gameId}` (1h TTL).

3. **Update `GameService`** — After pre-verification, enqueue job to Redis and return 202. Remove inline verify + persistence.

4. **Add `GET /api/scores/{gameId}/status`** — Reads result from Redis, returns pending/verified/rejected.

5. **Dockerfile.worker** — Multi-stage build.

6. **Docker Compose** — Add worker service.

7. **CI/CD** — Build + push worker image alongside API.

8. **Client** — Handle 202 + poll status in `ApiClient`/`ScoreSubmitter`.

9. **Tests** — Update integration tests for 202 flow.

## Open Questions

None.
