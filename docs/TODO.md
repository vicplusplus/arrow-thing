# TODO — Codebase Improvement Initiative

Cross-cutting improvement pass split into phased PRs. Each phase is its own PR.
The active phase for this branch (`claude/review-codebase-improvements-Pw341`) is **Phase 1: Security**.

## Active — Phase 1: Security

### 1.1 Idempotent score submission

**Problem.** `GameService.SubmitReplayAsync` only dedupes against *verified* scores
(`GameService.cs:107-131`). Between enqueue and completion, a client retry can
enqueue the same `gameId` twice, producing two verification jobs.

**Approach.**
- Before enqueue, attempt `SET verify:lock:{gameId} 1 NX EX {ttl}` in Redis.
- On success: enqueue job as today.
- On failure (lock already held): return the same `202 pending` response without enqueueing.
- TTL: 10 minutes (covers worst-case heavy-board verification).
- `VerificationWorker` releases the lock after writing the result payload.

**Why this layer.** Pre-verification is usually fast, but a client that retries on a
timeout can hit the same race. The lock is cheap and doesn't require a schema
migration.

**Tests.**
- Submit the same gameId twice → exactly one entry pushed to the queue.
- Second submission returns the same `{ gameId, status: "pending" }` shape.
- Lock is released after verification completes (both success and reject paths).

### 1.2 Email-change race condition

**Problem.** `ConfirmEmailChangeAsync` (`AuthService.cs:505-514`) does a
`AnyAsync(u => u.Email == …)` check before assigning `user.Email = pending`, with
no transaction. Two users confirming a change to the same address can both pass
the check and then race on SaveChanges.

**Approach.** The DB already has a unique index on `Users.Email`
(`AppDbContext.cs:30`). Catch `DbUpdateException` around the final
`SaveChangesAsync` and map unique-constraint violations to a 409 response, clearing
the pending fields.

**Tests.**
- Two concurrent `ConfirmEmailChange` requests targeting the same new address: one
  succeeds (200), the other fails (409), DB contains exactly one user at that
  address.

### 1.3 Weak JWT secret startup guard

**Problem.** Production must not boot with the dev default (`JWT_SECRET`
fallback in `local_startup.sh`) or a short secret.

**Approach.** After configuration loads in `Program.cs`, if
`builder.Environment.IsProduction()` and `Jwt:Secret` is empty / shorter than 32
bytes / matches a known dev default, throw at startup with a clear message.

**Tests.** Unit test that constructs a `WebApplicationBuilder` in Production with
a weak secret and asserts startup throws.

### 1.4 Admin key → authorization policy

**Problem.** `VerifyAdminKey` is called manually in five endpoint handlers
(`Program.cs:395-500`). One forgotten check = public admin endpoint.

**Approach.**
- Add an `AdminKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>`
  that reads `X-Admin-Key` and validates against `Admin:ApiKey` using
  `PasswordHasher.FixedTimeEquals`.
- Register scheme `"AdminKey"`.
- Add policy `"AdminKey"` requiring that scheme.
- Replace every manual check with `.RequireAuthorization("AdminKey")`.
- Delete the `VerifyAdminKey` helper.

**Tests.**
- Admin endpoint without header → 401.
- Admin endpoint with wrong key → 401.
- Admin endpoint with correct key → 200.

### 1.5 WebSocket message size cap

**Problem.** `CoopHub.HandleConnectionAsync` (`CoopHub.cs:228-239`) reads frames
into a `MemoryStream` with no upper bound. A malicious client can stream unbounded
binary into memory.

**Approach.** Track `ms.Length` inside the receive loop; if it exceeds 256 KB,
close the socket with `WebSocketCloseStatus.MessageTooBig` (1009) and break.

**Tests.** Unit test that feeds >256 KB to `HandleConnectionAsync` via a mock
WebSocket and asserts the close frame.

### 1.6 HTTPS redirection + HSTS

**Problem.** TLS is terminated at nginx but the ASP.NET app emits no HSTS header
and does not redirect HTTP→HTTPS if nginx ever misroutes.

**Approach.**
- Add `UseForwardedHeaders` with `ForwardedHeaders.XForwardedProto` (nginx already
  forwards it — see `Program.cs:715`).
- In non-Development: `app.UseHsts()` with 1-year max-age + preload, and
  `app.UseHttpsRedirection()`.

**Tests.** Integration test that a non-HTTPS request to the API gets a 307
redirect (with `X-Forwarded-Proto: http`), and that responses carry HSTS headers.

### 1.7 Document trust boundary in TechnicalDesign.md

Add a section under "Anti-cheat / verification":

> **Trust boundary.** The client is untrusted. Every score is re-simulated
> server-side by `ReplayVerifier` before it enters the leaderboard. Replay
> snapshots stored locally for playback are not trust anchors — snapshots are
> regenerated deterministically from seed on the server path. Any field the
> client can tamper with (gameId, seed, solve time, events) must be validated
> or re-derived before persistence.

Also note that top-50 replays store a gzipped board snapshot; others are
regenerated from seed on demand.

### Phase 1 done criteria

- All changes merged into `claude/review-codebase-improvements-Pw341`.
- `dotnet test server/ArrowThing.sln` green.
- Unity EditMode tests unaffected (no Domain changes).
- New tests listed above pass.
- TODO.md deleted before PR is merge-ready.

## Follow-up phases (separate PRs)

### Phase 1B: Auth features (larger scope)

- **New-device OTP.** On login, if fingerprint (hashed UA + IP /24) is new for
  this user, require an email 6-digit code before issuing the JWT. Reuse OTP
  infrastructure (`PasswordHasher.HashOtp`, 10-min TTL, work factor 8). New
  `UserDevice` table + schema migration + client modal.
- **JWT storage model on WebGL.** Move to HttpOnly cookie for the refresh token
  + in-memory access token. Server: cookie auth scheme + CORS credentials +
  CSRF token for mutating requests. Client: remove `PlayerPrefs` token storage,
  silent refresh on app start.

### Phase 2: Reliability

- **Redis circuit breaker / optional surfaces.** Wrap `IConnectionMultiplexer`
  in a small breaker; endpoints that depend on Redis return 503 instead of
  crashing. Don't throw at DI resolution; log + degrade.
- **Global exception handler.** Middleware that standardizes all error responses
  to `{ error, correlationId }` and logs unhandled exceptions with the
  correlation ID.
- **Email send failure surfacing.** Critical paths (register, password reset)
  return 503 on Resend failure instead of silent 200. Non-critical paths
  (already-registered notice) stay silent.
- **Canary deploy.** Roll API first with `docker compose up -d --no-deps api`,
  run health check, then roll workers. Rollback on health-check failure.

### Phase 3: Infra / deploy hardening

- **Docker secrets for prod compose** (`deploy/docker-compose.yml`). Dev compose
  stays on `.env`. Secrets as files under `/run/secrets/`, read by the app via
  `IConfiguration` sources.
- **Dockerfile hardening.** Non-root `USER` in both `Dockerfile` and
  `Dockerfile.worker`. Add `HEALTHCHECK`.
- **Tag format validation in `deploy.yml`.** Reject tags that don't match
  `^v[0-9]+\.[0-9]+\.[0-9]+$` before invoking the Unity build.
- **`.env.sample` cleanup.** Only runtime secrets + non-sensitive tunables; CI
  secrets (Discord webhook, Cloudflare token, Unity license) live in GitHub
  Secrets and must not appear in `.env.sample`.
- **ServerRotation.md completeness.** Audit against `admin.sh` capabilities; add
  rollback-to-previous-image, manual score removal, migration rollback.

### Phase 4: Storage / performance

- **Gzip replays server-side.** `Score.ReplayJson` becomes gzipped bytea; fetch
  path decompresses. Covers both snapshot-containing (top-50) and stripped
  replays. Expect 80%+ savings on DB size and /api/replays payloads.
- **Combined leaderboard COUNT query.** `LeaderboardService.GetPlayerEntryAsync`
  currently runs two separate COUNTs. Replace with a single query returning
  both rank and total.

### Phase 5: Accessibility & UX

- **Colorblind-friendly theme (or dedicated CB mode).** Current themes use hue
  to communicate clearable state in replay mode; retune so deutan/protan users
  can distinguish states. Decide: retune existing themes vs add a "High
  Contrast" theme.
- **Multi-touch input bug.** `InputHandler.cs:241` sets `_isDragging = true`
  whenever touch count ≥ 2 and never resets it until the touch ends. Two
  simultaneous taps on opposite ends of the screen are swallowed. Decouple
  pinch state from drag state.
- **Tap hit slop.** Expand cell hit rectangle by ~½ cell in screen space, but
  only resolve if a single arrow is within the expanded radius (ambiguous taps
  fall through or resolve to geometrically nearest). Not aim-assist — no bias
  toward clearable arrows.

### Phase 6: Docs cleanup

- Convert `CLAUDE.md` to a short pointer list ("architecture: see TDD";
  "contribution rules: see CONTRIBUTING.md"; "feature workflow: see below")
  instead of duplicating architecture content that drifts from
  `TechnicalDesign.md`.

## Explicitly skipped (considered and declined)

- Test result publishing via `dorny/test-reporter` — Unity's test reporter UX is
  too rough; local + GH logs are sufficient at current scale.
- Unity `Library/` cache on EditMode/PlayMode CI jobs — previously measured,
  cache hit time ≈ cold clone time.
- Clearing `FocusNavigator.WasKeyboardActive` on scene transitions — intentional
  behavior; keyboard users should not re-activate focus per scene.
