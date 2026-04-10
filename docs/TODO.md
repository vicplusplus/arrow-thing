# PR 2: Redis Infrastructure

Reference: `docs/AntiCheatDesign.md` § PR 2

## Plan

1. **Docker Compose** — Add Redis 7 Alpine to both dev and deploy compose files
   - Internal-only (expose, not ports)
   - AOF persistence, 256MB max memory, allkeys-lru eviction
   - Health check via `redis-cli ping`
   - Volume for persistence

2. **NuGet** — Add `StackExchange.Redis` to `ArrowThing.Server.csproj`

3. **DI registration** — Register `IConnectionMultiplexer` as singleton in `Program.cs`
   - Connection string from `Redis:ConnectionString` config key
   - Dev .env loader maps `REDIS_CONNECTION_STRING` env var

4. **Migrate LeaderboardCache to Redis**
   - Replace `ConcurrentDictionary` with Redis GET/SET
   - Keys: `leaderboard:{width}x{height}`, `leaderboard:all`
   - TTL: 5 minutes (or until invalidated)
   - Invalidate by DEL key
   - Serialize/deserialize `LeaderboardResponse` as JSON

5. **Config** — Update `.env.sample` with `REDIS_CONNECTION_STRING`

6. **Tests** — Add `Testcontainers.Redis` to test project, spin up Redis container in `TestFactory`

7. **Manual testing** — Verify leaderboard caching works end-to-end

## Open Questions

None — design is settled in AntiCheatDesign.md.
