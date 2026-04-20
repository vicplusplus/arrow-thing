# Release Checklist

Arrow Thing uses a staging-gated release flow. **Every** production release must first be soaked on staging and pass this checklist. The co-op v2.0 launch shipped without staging verification and broke in prod for a full day; this document exists so that doesn't happen again.

## Environments

| Environment | URL                              | Trigger                           |
| ----------- | -------------------------------- | --------------------------------- |
| Staging     | https://staging.arrow-thing.com  | Push to `main` → auto-deploy      |
| Production  | https://arrow-thing.com          | Published GitHub Release (tagged) |

Staging mirrors production (same Cloudflare edge, same VPS image layout, same nginx config), but uses a separate database, separate cookie domain, and a separate Cloudflare Pages project. Staging is safe to wipe.

## Release Flow

1. Merge feature PRs into `main`. Push auto-deploys client + server to staging.
2. Run the **Pre-Release Checklist** below against `https://staging.arrow-thing.com`.
3. If any item fails, fix on `main` and re-soak — do not cherry-pick around staging.
4. When all items pass, draft a GitHub release using `.github/release_template.md`. Tag format `v{x.y}` or `v{x.y.z}`.
5. Publish the release. Prod client + server deploy automatically.
6. Run the **Post-Deploy Smoke** on `https://arrow-thing.com`.
7. Announce (Discord workflow fires automatically on release publish).

## Pre-Release Checklist (run on staging)

Treat every unchecked box as a blocker.

### WebGL client

- [ ] Page loads cold (clear cache) without console errors.
- [ ] Git commit hash in footer matches the staging deploy commit.
- [ ] All shader variants render correctly — arrows, grid, particle effects, board-boundary glow. **Specifically verify in a WebGL build, not the editor.** Shader stripping only happens in builds.
- [ ] No missing material / magenta fallback anywhere in the single-player flow.
- [ ] Input works with keyboard, mouse, and touch (resize window to mobile width to test).
- [ ] Audio plays (unlocks on first user gesture — confirm).

### Auth + cookies

- [ ] Register → email verification → login round-trip works end-to-end.
- [ ] Session cookie is set with `SameSite=None; Secure` and the staging domain.
- [ ] Refresh after login keeps the session (cookie survives reload).
- [ ] Logout clears the cookie.
- [ ] Forgot-password flow sends and the reset link works.
- [ ] Rate-limited login (wrong password ×N) surfaces the correct error, not "unknown error".

### Co-op networking

- [ ] Create a room on one browser, join from a second browser (incognito / different account).
- [ ] WS handshake upgrades cleanly (check DevTools Network → WS, status 101).
- [ ] Both clients see the same board state after join.
- [ ] Moves propagate in both directions within a frame or two.
- [ ] Kill one client's network (DevTools → Offline), restore — client reconnects and resyncs.
- [ ] Host leaves → guest gets a clean "host disconnected" state, not a hang.
- [ ] Hub filters and room list refresh correctly.

### Leaderboards + replays

- [ ] Submit a run on staging → it appears on the staging leaderboard.
- [ ] Replay playback of the submitted run plays back identically.
- [ ] Self-rank shows only on the leaderboard screen, nowhere else.

### Server health

- [ ] `https://api-staging.arrow-thing.com/health` returns 200.
- [ ] Server logs contain no new ERROR-level entries during the soak.
- [ ] Migrations (if any) applied cleanly — check API container startup log.

## Post-Deploy Smoke (run on prod)

Minimum verification the prod deploy didn't regress anything obvious. Full checklist is only needed on staging.

- [ ] `https://arrow-thing.com` loads, commit hash matches the tag.
- [ ] Single-player game starts and a board is solvable.
- [ ] Login works for an existing account.
- [ ] `https://api.arrow-thing.com/health` returns 200.
- [ ] Create a co-op room and confirm WS upgrade.

## When something breaks in prod anyway

1. Rollback client: re-run the `Deploy WebGL to Cloudflare Pages` workflow on the previous release tag via `workflow_dispatch`.
2. Rollback server: SSH to VPS, `docker compose` with the previous image tag (images are tagged by SHA in `deploy-server.yml`).
3. File an incident note in `docs/` and add whatever check would have caught it to this checklist.
