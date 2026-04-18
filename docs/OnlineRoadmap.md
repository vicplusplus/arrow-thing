# Roadmap

## Current State (v0.7.4)

- **Arrow coloring** — implemented. `ArrowColoring.AssignColors()` in domain layer; `BoardView` applies palette colors after spawn.
- **Replay recording & verification** — implemented. `ReplayEvent`, `ReplayRecorder`, `ReplayData` (schema v4) in domain layer. Events recorded during play, persisted in save files, and submitted to the server for verification on completion. `ReplayVerifier` runs on the server (in the async worker) and reproduces the board from seed via `PortableRandom` for byte-for-byte determinism with the client.
- **Local saves / autosave** — implemented. Initial board snapshot persisted in save file; resumes without re-generation.
- **Local leaderboards & personal best** — implemented. `LeaderboardStore` (domain) + `LeaderboardManager` (view) with per-config/global caps, favorites, 3 sort criteria. Dedicated leaderboard scene with 5 size tabs + All, Local/Global toggle. Victory screen records results, detects personal best.
- **Global leaderboards** — implemented. Server-backed top-50 per-size and cross-size "All" tab, with player rank context, refresh button, and replay playback (top-50 carry a compressed snapshot; entries outside top-50 regenerate from seed on demand).
- **Replay viewer** — implemented. Dedicated scene with `ReplayViewController`, `ReplayPlayer` (domain), seek / speed (0.5×–10×) / play-pause controls, tap indicators, clearable highlighting (electric cyan with trail lanes). Accessed via play button on leaderboard entries.
- **Accounts & auth** — implemented. Email-based registration, login, verification, password reset, email/password change. JWT auth with SecurityStamp validation. Admin lock/unlock tooling. In-game account panel covering login, register, verify, forgot/reset password, account info, change email, confirm email, change password, and inline display name editing.
- **Server** — implemented. ASP.NET Core Minimal API on **.NET 10**, PostgreSQL, shared domain code via monorepo. Deployed via Docker Compose on a Hetzner VPS behind Cloudflare. Stack now also includes Redis (verification queue + leaderboard cache) and a standalone `ArrowThing.Worker` process for async score verification. CD pipeline builds and deploys on release.
- **Score integrity** — implemented. Synchronous pre-verification gate (replay schema version, board size, timing, seed dedup, account flag, rate limit) plus async full verification (board regeneration + clear simulation) on the worker. Casual cheaters get account-flagged; flagged users are excluded from leaderboards. See [`docs/AntiCheatDesign.md`](AntiCheatDesign.md) for design history and `TechnicalDesign.md § Score Integrity` for the authoritative spec.
- **UI theming** — implemented. CSS custom property system with runtime theme switching. 4 themes (Dark, Light, Dark Monochrome, Light Monochrome). Shared reusable UI component library.
- **Keyboard navigation** — implemented. `FocusNavigator` directed-graph nav, `KeybindManager` runtime InputActionAsset with rebindable keybinds, `NavigationCoverageTests` enforce that every UXML button is keyboard-reachable in some scene state. Gameplay/leaderboard/replay shortcuts.
- **Global toast** — implemented. `GlobalToast` `DontDestroyOnLoad` singleton survives scene transitions, used by score submission for retry/dismiss UX on transient failures.
- **Observability** — implemented. Serilog → Loki, OpenTelemetry → Prometheus, Grafana dashboards (server health, admin, scores) with PostgreSQL SQL datasource for audit log queries.
- **Server CD** — implemented. Docker image → GitHub Container Registry → SSH deploy to VPS for both `api` and `worker`. Health check on deploy. Discord release announcements.

Versions are tagged when a coherent chunk of work lands, not on a fixed schedule.

---

## Implemented Features

### Server Foundation

- **ASP.NET Core Minimal API on .NET 10** — lightweight (~30-50 MB idle RAM in Docker), C#, shares domain code. Use Workstation GC in container (`<ServerGarbageCollection>false</ServerGarbageCollection>`) for lower memory on small VPS.
- **Entity Framework Core** — ORM. PostgreSQL everywhere (production and integration tests via Testcontainers). SQLite dropped.
- **Redis** — verification job queue (`verify:queue`) and result cache (`verify:result:{gameId}`), plus global leaderboard response cache. Internal-only Docker network exposure.
- **`ArrowThing.Worker`** — standalone .NET 10 console worker that consumes the verification queue, runs `ReplayVerifier.Verify`, and persists verified scores. Runs as a separate Docker service so verification CPU work never blocks API request threads.
- **BCrypt** — password hashing.
- **JWT** — stateless auth tokens.

### VPS Hosting

**Provider**: Hetzner Cloud CCX13 — 2 dedicated vCPU (AMD), 8 GB RAM, 80 GB SSD, ~$19.99/mo. Ashburn (US East) datacenter. IPv6-only (no IPv4 add-on); Cloudflare proxy provides IPv4 reachability for clients.

**Stack**: Docker Compose (ASP.NET API + verification worker + PostgreSQL + Redis + Loki/Prometheus/Grafana) behind an Nginx reverse proxy, fronted by Cloudflare for TLS termination, IPv4→IPv6 translation, and DDoS protection.

**Current state**: VPS provisioned and hardened (SSH key-only, UFW, fail2ban, unattended-upgrades, Docker). Origin certs and `.env` in place. Backup and disk monitoring cron jobs installed. CI SSH key authorized. Deploy configs version-controlled in `server/deploy/`.

#### VPS Layout

```
/home/deploy/
├── arrow-thing/                        # live deployment directory
│   ├── docker-compose.yml              # from repo (server/deploy/)
│   ├── init-db.sh                      # from repo (server/deploy/)
│   ├── .env                            # manual (secrets, not in repo)
│   └── nginx/
│       ├── nginx.conf                  # from repo (server/deploy/nginx/)
│       └── certs/
│           ├── origin.pem              # manual (Cloudflare origin cert)
│           ├── origin-key.pem          # manual (origin private key)
│           └── cloudflare-origin-pull.pem  # downloaded by setup.sh
├── repo/                               # git clone of the project
└── backups/                            # daily pg_dump output
```

Run `server/deploy/setup.sh` from the repo root to sync configs, validate nginx, and install cron jobs.

#### Docker Compose — services

- **api** — ASP.NET Core app. Exposes port 5000 internally only. Reads connection string and JWT secret from environment. Enqueues score-verification jobs to Redis.
- **worker** — `ArrowThing.Worker` console process. Consumes the Redis verification queue, runs `ReplayVerifier.Verify`, persists verified scores, writes results back to Redis with a 1-hour TTL. No published ports.
- **db** — PostgreSQL 16. Named volume for data persistence (`pgdata`). Not exposed to host network. Init script grants DML-only privileges to the app user.
- **redis** — Redis 7 Alpine. Verification queue, verification result cache, global leaderboard response cache. Internal-only (`expose:`), `appendonly yes`, `maxmemory 256mb`, `allkeys-lru`.
- **nginx** — Reverse proxy. Only service with published ports (80, 443). Cloudflare Origin cert for Full (Strict) TLS. Authenticated origin pulls verify requests come from Cloudflare. Rate limiting on auth endpoints (5 req/min) and general API (30 req/min), keyed on `CF-Connecting-IP`. CORS restricted to `https://arrow-thing.com`.
- **loki / prometheus / grafana** — observability stack. None publicly exposed; Grafana binds to `127.0.0.1:3000` for SSH-tunnel access only.

Docker bypasses UFW for published ports — this is why only nginx has `ports:` and all inter-container communication uses Docker's internal DNS.

#### Cloudflare Configuration (arrow-thing.com)

| Setting | Value |
|---------|-------|
| **DNS**: `api` AAAA | `<vps-ipv6>`, proxied |
| **DNS**: `api` A | `<vps-ipv4>`, proxied |
| **DNS**: `@`, `www` | Cloudflare Pages (automatic) |
| **Pages project** | `arrow-thing`, deployed via `cloudflare/wrangler-action@v3` |
| **Pages custom domains** | `arrow-thing.com`, `www.arrow-thing.com` |
| **SSL/TLS mode** | Full (Strict) |
| **Origin certificate** | ECC, 15-year, `api.arrow-thing.com` |
| **Authenticated Origin Pulls** | Enabled (zone-level) |
| **Redirect Rule** | `www.arrow-thing.com*` → `https://arrow-thing.com${1}` (301) |
| **Cache Rule** | `api.arrow-thing.com` → bypass cache |
| **Rate Limiting Rule** | `/api/*` → block 10s after 60 req/10s per IP |

**Hetzner Cloud Firewall**: inbound TCP 22, 80, 443 from any; default-allow outbound. Actual IPs are stored in `VPS_HOST` GitHub secret, not committed to the repo.

Both an A (IPv4) and AAAA (IPv6) record point to the server. The A record is required because GitHub Actions runners are IPv4-only and need to reach the server for the post-deploy health check.

#### CI/CD Deployment

- **WebGL**: GitHub Actions builds Unity, deploys to Cloudflare Pages via Wrangler. Split into build + deploy jobs.
- **API**: GitHub Actions workflow builds Docker image → pushes to `ghcr.io/vicplusplus/arrow-thing-api` → SSH to VPS → `docker compose up -d api` → health check with 6 retries.
- **GitHub secrets**: `CLOUDFLARE_ACCOUNT_ID`, `CLOUDFLARE_API_TOKEN`, `DISCORD_WEBHOOK_URL`, `UNITY_EMAIL`, `UNITY_LICENSE`, `UNITY_PASSWORD`, `VPS_HOST`, `VPS_SSH_KEY`.

#### Backups & Monitoring

- **Backups**: daily `pg_dump` at 04:00 UTC, gzipped, 14-day retention. Installed via `setup.sh`.
- **Disk monitoring**: cron alert every 6 hours if usage exceeds 80%.
- **Docker restart policy**: `restart: unless-stopped` on all services.
- **Logging**: JSON log driver, `max-size: 10m`, `max-file: 3`.
- **External uptime**: UptimeRobot (free tier) on `https://api.arrow-thing.com/health` (to be set up after first deploy).
- **Database connection pooling**: EF Core default; PgBouncer sidecar if needed later.

#### Post-First-Deploy Checklist

- Verify all three containers start: `docker compose up -d`, check `docker ps`.
- Health check: `curl -f https://api.arrow-thing.com/health` returns 200.
- Restart policy: `docker kill arrow-thing-api-1` → auto-restart. Reboot → all containers running.
- Test backup restore after first real data.
- Restrict UFW ports 80/443 to [Cloudflare IP ranges](https://www.cloudflare.com/ips/) only.
- Final review: `sudo ufw status verbose`, `docker ps --format "{{.Ports}}"`, verify Postgres not reachable from host.

### Accounts

Email-based authentication with verification, password reset, and email change flows. No OAuth, no usernames — email is the sole login identifier.

- **Register** with email + password + display name → receive JWT. Verification email sent via Resend.
- **Login** with email + password → receive JWT. Locked accounts receive 403.
- JWT included in `Authorization: Bearer` header for authenticated endpoints.
- **Email**: unique, case-insensitive, used for login. Must be verified to submit scores to leaderboards.
- **Display names**: shown on leaderboards. 2-24 chars, allows spaces and Unicode. Changeable anytime. Not required to be unique.
- **Passwords**: minimum 8 chars, BCrypt hashed.
- **SecurityStamp**: included in JWT, validated on every authenticated request. Bumping invalidates all existing tokens.

**Email flows** (via Resend HTTP API) — all flows use 6-digit codes entered in-app (no browser pages):
- **Email verification**: 6-digit code emailed on registration. 10-minute expiry. Resend with 5-minute cooldown.
- **Password reset**: 6-digit code emailed on forgot-password request. 10-minute expiry. 5-minute cooldown.
- **Email change**: requires current password. 6-digit code sent to new email (10-min expiry). Notification sent to old email referencing Discord for support. Race-condition safe (checks uniqueness at confirmation time).

**Admin tooling** (protected by `X-Admin-Key` header, not JWT):
- **Lock account**: sets `LockedAt`, clears all tokens, reverts pending email changes, bumps SecurityStamp (invalidates all JWTs). Locked accounts cannot log in (403).
- **Unlock account**: clears `LockedAt`, generates password reset code, sends reset email.

**Client UI**:
- **Account icon button** in the **top-right** of the main menu. Always visible.
  - **Not logged in**: full-screen account panel with login (default), register, forgot password forms.
  - **Logged in**: account info (masked email, verify status, display name change, change email, logout).
- **`AccountManager`** (view layer) — manages 10 forms: login, register, verify code, forgot password, reset password, account info, change email, confirm email code, change password, change display name. Calls `GetMeAsync()` on account info show to refresh state. All forms clear fields on navigation.
- **`ApiClient`** (view layer) — HTTP client wrapper. Attaches JWT. Handles errors. Stores token/display name/email verified in `PlayerPrefs`.
- No separate "Online" gate — the game is always playable. Logged-in users automatically submit scores; logged-out users play offline.

## Planned Features

### Co-op boards — **implemented**

Shipped through the eight-phase plan in [`docs/CoopRoadmap.md`](CoopRoadmap.md). Persistent shared puzzles that any number of registered players can chip away at, in real time when they overlap and asynchronously when they don't. Per-player timer + clear count with a live sidebar, per-lobby results screen, snapshot-based replay playback with playerId tinting. Built on a new WebSocket session layer on top of the existing REST server. See the CoopRoadmap for the phase-by-phase status; `docs/TechnicalDesign.md § Co-op Server` is the authoritative architecture reference.

### PvP

Real-time garbage mechanics, matchmaking. The replay viewer is essentially a live opponent board — the framework from the replay and server work carries over directly.

### Known limitations of the current online stack

- **Bots/automation**: a bot could solve boards optimally. Pre-verification + account flagging cover casual cheaters; sophisticated bots are accepted as unstoppable. The leaderboard is friendly competition, not a ranked ladder. See `TechnicalDesign.md § Score Integrity` for the threat model.
- **Timing manipulation**: client reports input timestamps. A modified client could lie. The pre-verification gate rejects implausibly fast inter-event gaps as a basic sanity check. Full solution would require server-witnessed timing (a WebSocket path), which is incompatible with async play across days/weeks and has been ruled out.

---

## Architecture

### High-Level Online Flow

```
Client                              API Server                       Worker
──────                              ──────────                       ──────
1. Generate board locally           (always local — no server round-trip)
   from random seed (PortableRandom xorshift32 — same on client and server)

2. Play game, record input
   events: [{ seq, type, posX, posY, timestamp }]

3. [online only, if logged in
   and email verified]
   POST /api/scores      ────────►  Pre-verify (replay version, board
   { replayJson }                   size, timing, seed dedup, account
                                    flag, rate limit)
                                    Enqueue to Redis verify:queue
                         ◄────────  202 Accepted { gameId, status: "pending" }
                                                                     │
                                                                     ▼
                                                                     BRPOP verify:queue
                                                                     ReplayVerifier.Verify()
                                                                     Persist score on success
                                                                     Write result to
                                                                     verify:result:{gameId}

4. GET /api/scores/      ────────►  Read verify:result:{gameId}
   {gameId}/status       ◄────────  { status, rank, isPersonalBest, reason? }

5. View leaderboards     ────────►  Query by board config (Redis-cached)
                         ◄────────  Return ranked entries (live display names)
```

### Why This Works

- **Deterministic generation**: `Board` + `BoardGeneration` + `PortableRandom` (xorshift32) = byte-for-byte identical boards on Unity client and .NET server. No board state needs to be streamed.
- **Minimal bandwidth**: Only seed + input events travel over the wire. A full game replay is ~50-200 input events (one per arrow cleared).
- **Async verification**: Pre-verification runs synchronously to reject obviously bad submissions; full verification (board regeneration + clear simulation) runs in a separate worker process so the API thread pool is never blocked by large boards.
- **Cheat resistance**: Server is authoritative — it regenerates the board and simulates the solve. Fabricated replays that skip arrows or claim impossible clears are rejected. Casual cheaters get account-flagged.
- **Shared code**: The domain layer (`Assets/Scripts/Domain/`) is Unity-independent pure C#. The server's `ArrowThing.Domain` (`netstandard2.1`) project references the same source files directly via wildcard `<Compile Include>`. Zero duplication of game logic.
- **Offline-first**: The client can always generate boards locally. Server connection is a bonus (enables leaderboard submission), not a requirement. The game never blocks on network.

---

## Replay Format

JSON. One file per game session. Current schema version: **4** (see `ReplayVersionPolicy.MinReplayVersion`). Replays from clients on schema < 4 are rejected up-front by the score endpoint with `426 Upgrade Required`.

```jsonc
{
  "version": 4,
  "gameId": "uuid",
  "seed": 12345,
  "boardWidth": 20,
  "boardHeight": 20,
  "maxArrowLength": 40,
  "inspectionDuration": 15.0,
  "boardSnapshot": [
    [{ "X": 0, "Y": 0 }, { "X": 0, "Y": 1 }, { "X": 0, "Y": 2 }],
    // ... one sub-array per arrow (head-to-tail cell order)
  ],
  "events": [
    { "seq": 0, "type": "session_start", "timestamp": "2026-03-19T12:00:00.000Z" },
    { "seq": 1, "type": "start_solve",   "timestamp": "2026-03-19T12:00:15.000Z" },
    { "seq": 2, "type": "clear",         "posX": 5.23, "posY": 12.41, "timestamp": "2026-03-19T12:00:15.000Z" },
    { "seq": 3, "type": "clear",         "posX": 3.10, "posY": 7.67,  "timestamp": "2026-03-19T12:00:15.342Z" },
    { "seq": 4, "type": "reject",        "posX": 10.48, "posY": 4.15, "timestamp": "2026-03-19T12:00:15.781Z" },
    // ...
    { "seq": N, "type": "clear",         "posX": 8.31, "posY": 1.72,  "timestamp": "2026-03-19T12:00:29.529Z" },
    { "seq": N+1, "type": "end_solve",   "timestamp": "2026-03-19T12:00:29.529Z" }
  ],
  "finalTime": 14.529
}
```

- `seq` — monotonically increasing sequence number. **Defines event order.** Timestamps can tie (see below), but `seq` never does.
- `type` — `session_start`, `session_leave`, `session_rejoin`, `start_solve`, `clear`, `reject`, `end_solve`. Rejects are recorded for replay playback fidelity but don't affect verification. Session events are for save/resume bookkeeping.
- `posX`, `posY` — world-space coordinates of the tap. **Present only on `clear` and `reject` events** (omitted from JSON for other types via Newtonsoft `NullValueHandling.Ignore`). The cell is derived via `BoardCoords` (floor to grid cell). Storing the exact position enables the replay viewer to show a tap indicator at the precise location the player tapped.
- `timestamp` — wall-clock time in ISO 8601 UTC. Present on all events. Solve-relative timing is derived by subtracting the `start_solve` timestamp, excluding any `session_leave`→`session_rejoin` gaps.
- `boardSnapshot` — the initial arrow configuration (all arrows before any clears). Each sub-array is one arrow's cells in head-to-tail order. Used for fast resume and replay playback without regeneration.
- `finalTime` — solve time in seconds, derived from event timestamps. Server verifies this matches.

#### Timestamp Ties: `start_solve` + `clear`

The first arrow clear also starts the solve timer (see `InputHandler` / `GameTimer.StartSolve`). This means a single input event produces two replay events with **identical timestamps**: `start_solve` (seq N) followed by `clear` (seq N+1). The verifier must process events by `seq`, not `timestamp`. Timestamps are for timing measurement and replay playback pacing.

### Domain Types for Replay

- **`ReplayEvent`** (domain, implemented) — `int seq`, `string type`, nullable `float? posX`/`posY` (clear/reject only), `string timestamp` (ISO 8601 UTC). Seq is auto-assigned by the recorder. Serialized via Newtonsoft; null fields omitted from JSON.
- **`ReplayRecorder`** (domain, implemented) — accumulates events during play. `Record(type, posX, posY, timestamp)` auto-increments seq. `ToReplayData()` returns the serializable replay.
- **`ReplayVerifier`** (domain, done) — static class. Takes `ReplayData`, regenerates board from seed, compares to snapshot, simulates clears via `(int)Math.Round(posX/posY)`, returns `VerificationResult` (valid/invalid + reason + verified time).
- **`InputHandler`** changes (implemented) — calls `ReplayRecorder.Record()` on each tap (clear or reject), passing world-space tap position.

---

## Server

### Project Structure

```
server/
├── ArrowThing.Server/           # ASP.NET Core web API (net10.0)
│   ├── Program.cs               # Minimal API endpoints, DB + JWT middleware wiring
│   ├── Auth/                    # (implemented)
│   │   ├── AuthService.cs       # All auth operations (register, login, verify, reset, email change, lock/unlock)
│   │   ├── AuthDtos.cs          # Request/response records
│   │   ├── PasswordHasher.cs    # BCrypt wrapper
│   │   ├── JwtHelper.cs         # HMAC-SHA256 token generation + validation (SecurityStamp claim)
│   │   ├── IEmailService.cs     # Email service interface
│   │   └── EmailService.cs      # Resend HTTP API wrapper
│   ├── Games/                   # (implemented)
│   │   ├── ScoreService.cs              # Pre-verify + enqueue to Redis verify:queue
│   │   ├── ScorePersistenceService.cs   # Worker-callable: persist verified score, manage snapshot strategy
│   │   └── ReplayVersionPolicy.cs       # MinReplayVersion gate
│   ├── Leaderboards/            # (implemented)
│   │   ├── LeaderboardService.cs
│   │   └── LeaderboardCache.cs          # Redis-backed
│   ├── Data/                    # (implemented)
│   │   ├── AppDbContext.cs      # EF Core context with Users, Scores, AuditLogs DbSets
│   │   └── Migrations/          # CreateUsers, AddEmailAndTokens, AddPendingEmailChange, RemoveUsername, AddScores, AddFlagging, …
│   └── Models/                  # (implemented)
│       ├── User.cs              # Id, Email, DisplayName, PasswordHash, SecurityStamp, verification/reset/email-change code fields, lock fields, Flagged/FlagReason
│       ├── Score.cs             # Id, UserId, GameId, Seed, BoardWidth, BoardHeight, MaxArrowLength, Time, ReplayJson, CreatedAt, UpdatedAt
│       └── AuditLog.cs          # Auth event audit trail
├── ArrowThing.Worker/           # Verification worker (net10.0, console)
│   ├── Program.cs               # Host builder + worker registration
│   └── VerificationWorker.cs    # BRPOP verify:queue → ReplayVerifier.Verify → persist → write result
├── ArrowThing.Domain/           # Shared domain code (netstandard2.1, C# 9)
└── ArrowThing.Server.Tests/     # xUnit integration tests (auth, scores, leaderboards, replays, anti-cheat)
```

### API Endpoints

```
GET    /health                                                       → 200 OK                                          [implemented]

POST   /api/auth/register        { email, password, displayName }    → { message }                                     [implemented]
POST   /api/auth/login           { email, password }                 → { token, displayName, emailVerified }           [implemented]
GET    /api/auth/me              [auth]                              → { email, displayName, emailVerified }           [implemented]
PATCH  /api/auth/me              [auth] { displayName }              → { displayName }                                 [implemented]

POST   /api/auth/verify-code            { email, code }              → { token, displayName, emailVerified }           [implemented]
POST   /api/auth/resend-verification    { email }                    → { message }                                     [implemented]
POST   /api/auth/forgot-password        { email }                    → { message }                                     [implemented]
POST   /api/auth/reset-password         { email, code, newPassword } → { message }                                     [implemented]
POST   /api/auth/change-password [auth] { currentPassword, newPwd }  → { message }                                     [implemented]
POST   /api/auth/change-email    [auth] { newEmail, currentPassword }→ { message }                                     [implemented]
POST   /api/auth/confirm-email-change [auth] { email, code }         → { message }                                     [implemented]

POST   /api/admin/lock-account   [admin] { email }                   → { message }                                     [implemented]
POST   /api/admin/unlock-account [admin] { email }                   → { message }                                     [implemented]

POST   /api/scores               [auth] { replayJson }              → 202 { gameId, status: "pending" }                [implemented]
GET    /api/scores/{gameId}/status [auth]                            → { status, rank?, isPersonalBest?, reason? }      [implemented]

GET    /api/leaderboards/{w}x{h} ?limit=50                          → { entries: [{ rank, displayName, time, gameId }] } [implemented]
GET    /api/leaderboards/all     ?limit=50                          → { entries: [{ rank, displayName, time, gameId, boardWidth, boardHeight }] } [implemented]
GET    /api/leaderboards/{w}x{h}/me [auth]                          → { rank, time, gameId } | 404                    [implemented]
GET    /api/leaderboards/all/me     [auth]                          → { rank, time, gameId, boardWidth, boardHeight } | 404 [implemented]

GET    /api/replays/{gameId}                                         → { replayJson } | 404 (verified top-50 only have snapshot) [implemented]

GET    /api/admin/flagged-users          [admin]                     → list of flagged users with reasons              [implemented]
POST   /api/admin/users/{id}/unflag      [admin]                     → { message }                                     [implemented]
POST   /api/admin/scores/{id}/remove     [admin]                     → { message }                                     [implemented]
```

### Domain Code Sharing

The domain layer (`Cell`, `Arrow`, `Board`, `BoardGeneration`, `ReplayVerifier`) must compile without Unity references. Current state: already true — all domain code is pure C#.

**Approach**: Monorepo with a shared `ArrowThing.Domain.csproj` that compiles the domain source files via relative paths. No symlinks, no NuGet packages, no file copies. Unity continues using the loose `.cs` files directly; the server references the shared project. **Implemented** — domain builds clean against Unity sources, pinned to C# 9 / netstandard2.1 for Unity compatibility. Newtonsoft.Json added as NuGet dependency (Unity ships it natively).

```
arrow-thing/                              # monorepo root
├── Assets/Scripts/Domain/                # source of truth (Unity uses directly)
├── server/
│   ├── ArrowThing.sln                    # solution file for all server projects
│   ├── ArrowThing.Domain/
│   │   └── ArrowThing.Domain.csproj      # netstandard2.1 C# 9, <Compile Include="../../Assets/Scripts/Domain/**/*.cs" />
│   ├── ArrowThing.Server/                # ASP.NET Core net10.0, <ProjectReference> to Domain
│   │   └── ArrowThing.Server.csproj
│   ├── ArrowThing.Worker/                # net10.0 console worker, <ProjectReference> to Domain
│   │   └── ArrowThing.Worker.csproj
│   └── ArrowThing.Server.Tests/          # xUnit integration tests, <ProjectReference> to Server
│       └── ArrowThing.Server.Tests.csproj
```

The domain `.csproj` targets `netstandard2.1` with `LangVersion 9` for compatibility with Unity's C# 9 compiler. The server and worker target `net10.0`. No code duplication — one source of truth, three consumers (Unity, server, worker).

---

## Leaderboards

### Partitioning

One leaderboard per board configuration:
- **Small** — 10×10
- **Medium** — 20×20
- **Large** — 40×40
- **XLarge** — 100×100

Future board sizes automatically create new partitions (no code change needed — partitioning is by `(width, height)` tuple).

An **"All"** tab ranks players globally across all sizes: biggest board cleared first (area DESC), then fastest time within that size. Each player's representative score is their best time on their largest completed board.

### Display

Two contexts: dedicated leaderboard scene and victory screen inline.

#### Dedicated Leaderboard Scene

Accessed via a trophy button in the **top-right of the main menu and the solo size select screen**.

- **Top 50** entries per partition, showing rank, display name, and time.
- **Tabs** for each board size (Small / Medium / Large / XLarge / All), abbreviated to S/M/L/XL/All on narrow viewports.
- **Toggle**: Local vs Global leaderboards.
  - **Global**: fetched from server. Only verified online scores. Top-3 entries get gold/silver/bronze tints.
  - **Local**: stored on-device under `Application.persistentDataPath` (backed by `IndexedDB` on WebGL). Includes both online and offline scores. Not synced to server. Capped at top 50 entries + replays per board size to keep storage bounded.
- **Play replay button** on each entry — loads the replay for that score. Replays for local scores are stored locally alongside the leaderboard data as GZip-compressed JSON. Global replays are fetched via `GET /api/replays/{gameId}` (top-50 carry an embedded board snapshot; entries outside top-50 regenerate the board from seed on demand).

#### Victory Popup

Shown after the board-clear animation. The victory modal does **not** embed a leaderboard preview — players use the explicit "View Leaderboard" button to navigate to the dedicated scene (which auto-scrolls to the freshly recorded entry via `GameSettings.LeaderboardFocusGameId`).

- If the score is a **new personal best** (compared against local scores):
  - Timer text turns **bright gold** during the board-clear sequence (before the modal appears).
- Score submission to the server happens silently in the background during the victory animation via the fire-and-forget `ScoreSubmitter`. On a transient failure (network, 5xx, 429) a toast appears in the top-right of the victory overlay with a Retry button. On 202 + still-pending status after polling, an info toast surfaces via `GlobalToast` and survives the scene transition.

### Score Model

```
User:
  Id                          GUID
  Email                       string (unique, case-insensitive, login identifier)
  DisplayName                 string (shown on leaderboards, changeable)
  PasswordHash                string (BCrypt)
  SecurityStamp               string (GUID, included in JWT, bumped to invalidate sessions)
  CreatedAt                   DateTime
  EmailVerifiedAt             DateTime? (null = unverified)
  VerificationCode            string? (6-digit code)
  VerificationCodeExpiresAt   DateTime?
  LastVerificationEmailAt     DateTime?
  PasswordResetCode           string? (6-digit code)
  PasswordResetCodeExpiresAt  DateTime?
  LastPasswordResetEmailAt    DateTime?
  PendingEmail                string? (new email awaiting confirmation)
  PendingEmailCode            string? (6-digit code)
  PendingEmailCodeExpiresAt   DateTime?
  LockedAt                    DateTime? (non-null = locked, blocks login)

  Flagged                     bool (default false; cheaters; excluded from leaderboards and blocked from submitting)
  FlagReason                  string?

Score:
  Id              GUID
  UserId          FK → User
  GameId          GUID (client-generated, identifies the game that produced this PB)
  Seed            int
  BoardWidth      int
  BoardHeight     int
  MaxArrowLength  int
  Time            double (seconds, server-verified)
  ReplayJson      TEXT  (full ReplayData; boardSnapshot gzip-base64 encoded if top-50, stripped otherwise)

AuditLog:
  Id          GUID
  Timestamp   timestamptz
  EventType   string  (14 types: register, login_success, login_failure, password_change, …)
  UserId      GUID?
  Email       string?
  ClientIp    string?  (from X-Forwarded-For)
  Detail      string?
```

One Score row per `(UserId, BoardWidth, BoardHeight)` — the player's personal best for that size. Updated in-place when a better verified time is submitted. Top-50 scores carry a compressed board snapshot for instant replay loading; scores outside top-50 have the snapshot stripped (board is regenerated from seed+params on replay view). Scores displaced from top-50 have their snapshot immediately removed.

---

## Implemented Scripts

All listed scripts are implemented and shipping. Refer to `docs/TechnicalDesign.md` for the authoritative class roster — this list is kept for historical roadmap context.

| Script | Layer | Purpose |
|--------|-------|---------|
| `LeaderboardEntry` | Domain | One leaderboard entry (metadata, no replay data) |
| `LeaderboardStore` | Domain | Pure C# leaderboard storage with caps, sorting, favorites |
| `ReplayPlayer` | Domain | Time-based replay playback engine with speed/seek |
| `ReplayVerifier` | Domain | Simulates replay for server-side verification (snapshot comparison, event validation, solve time computation) |
| `VerificationResult` | Domain | Result type for ReplayVerifier (validity, reason, verified time) |
| `ReplayVersionPolicy` | Domain (server) | `MinReplayVersion` gate for replay schema upgrades |
| `LeaderboardManager` | View | Singleton persistence layer (file I/O, GZip replays) |
| `LeaderboardScreenController` | View | Dedicated leaderboard scene: tabs, sorts, context menu, auto-scroll |
| `BoardSetupHelper` | View | Shared board/view/camera setup (extracted from GameController) |
| `ReplayViewController` | View | Replay viewer scene: playback, seek, controls, highlighting |
| `TapIndicator` / `TapIndicatorPool` | View | Expanding/fading ring at tap position during replay |
| `ApiClient` | View | HTTP client, JWT attachment, all auth + leaderboard endpoints, token storage in PlayerPrefs |
| `AccountManager` | View | Account panel: login/register, verify, forgot/reset password, account info, change email, confirm email, change password, inline display name editing |
| `ScoreSubmitter` | View | Fire-and-forget score submission helper (checks login state, serializes, polls 202 status, surfaces toasts on failure) |
| `GlobalToast` | View | `DontDestroyOnLoad` toast singleton with retry/dismiss buttons |
| `ConfirmModal` | View | Reusable confirm modal wrapper |
| `ServerHealthCheck` | Editor | Editor menu item (Tools > Arrow Thing) to test server connectivity |

---

## Testing Plan

### Automated (NUnit EditMode)
- `ReplayVerifier`: valid replays pass, invalid replays (wrong cell, skipped arrow, bad order) fail
- `GenerationFingerprintTests`: identical generated boards across Unity and .NET runtimes for the same seed (anti-regression for the `PortableRandom` requirement)

### Automated (Server Integration — xUnit, Testcontainers)
- Auth: register, login (email-based), duplicate email rejection, validation errors
- Auth: display name change (`PATCH /api/auth/me`), `GET /api/auth/me`
- Email verification: verify code, resend with rate limiting
- Password reset: forgot password, reset with code, expired code
- Email change: request, confirm, wrong password, same email, invalid code, race condition (email taken)
- Account lock/unlock: lock invalidates sessions + blocks login, unlock sends reset email + allows recovery
- Admin: valid/invalid/missing X-Admin-Key
- Score submission (pre-verify): replay version gate (426), board size, timing, seed dedup, account flag
- Score submission (worker): valid replay accepted via async worker, personal best upserted, rank returned
- Score submission: invalid replay rejected with reason (tampered events, tampered snapshot)
- Score submission: slower time leaves existing record unchanged
- Score submission: same gameId idempotent
- Score submission: rate limit enforced
- Leaderboards: ranking correctness, partitioning by board size, personal best query, flagged users excluded
- Replays: fetch returns JSON for verified score; 404 for unknown
- Display name: rename reflected in leaderboard
- E2E verification: end-to-end fingerprint tests for board sizes 5×5 through 50×50

### Manual
- **Full online flow**: register → play → submit → see score on leaderboard → play replay
- **Offline play**: server unreachable → game works normally → score appears on local leaderboard only → no errors
- **Account UI**: account section in Settings → login/register → email verification → display name change → email change → logout
- **Victory personal best**: gold timer during board-clear sequence
- **Leaderboard scene**: tabs, local/global toggle, top 50, replay playback (top-50 instant load, outside-top-50 regenerated from seed)
- **Error handling**: server down mid-submission, network timeout, expired token, retry toast
