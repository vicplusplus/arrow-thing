# Local Server Setup

How to run the Arrow Thing server locally for development and testing.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (for PostgreSQL)
- `dotnet-ef` CLI tool (for migrations):
  ```
  dotnet tool install --global dotnet-ef
  ```

## Quick Start

Start PostgreSQL via Docker Compose:

```bash
cd server
docker compose -f docker-compose.dev.yml up -d
```

Configure `server/.env` with your local settings:

```
POSTGRES_USER=postgres
POSTGRES_PASSWORD=dev
POSTGRES_DB=arrowthing
POSTGRES_HOST=localhost
JWT_SECRET=dev-only-secret-do-not-use-in-production-at-least-32-bytes!
ADMIN_API_KEY=test-admin-key
RESEND_API_KEY=
GRAFANA_ADMIN_PASSWORD=
```

Then run:

```bash
cd server
dotnet run --project ArrowThing.Server
```

The server starts at **http://localhost:5000**. Migrations are applied automatically on startup.

## Configuration

In development mode, the server loads environment variables from `server/.env` and maps them to ASP.NET Core configuration keys (see `Program.cs`). The `.env` file is gitignored.

| `.env` variable | Maps to config key | Purpose |
|---|---|---|
| `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `POSTGRES_HOST` | `ConnectionStrings:Default` | PostgreSQL connection |
| `JWT_SECRET` | `Jwt:Secret` | JWT signing key |
| `ADMIN_API_KEY` | `Admin:ApiKey` | Admin endpoint auth |
| `RESEND_API_KEY` | `Resend:ApiKey` | Email service (Resend) |

Fallback: `appsettings.Development.json` provides a dev JWT secret and Resend from-address. User secrets (`dotnet user-secrets`) also work and take priority over `.env`.

### Email (Resend)

Email features (verification codes, password reset) require a [Resend](https://resend.com/) API key in `.env`. Without it, registration succeeds but no verification email is sent — check server logs for the error. Verification codes are BCrypt-hashed in the database and cannot be read directly.

## Running Tests

```bash
cd server
dotnet test
```

Tests use **Testcontainers** to spin up a throwaway `postgres:16-alpine` container and a **fake email service** via `TestFactory` — they don't touch your local database or send real emails. Docker must be running.

## Unity Client → Local Server

When running in the **Unity Editor**, `ApiClient` automatically uses `http://localhost:5000`. In builds, it points to `https://api.arrow-thing.com`.

Make sure the server is running before testing account or leaderboard features in the editor.

### WebGL build → Local Server

WebGL builds (needed to test multiplayer against a local server) resolve the API URL at boot via `Assets/Plugins/WebGL/ApiUrlOverride.jslib`:

1. **Serve the WebGL build from `localhost` / `127.0.0.1`.** The jslib auto-detects the hostname and rewrites the API base to `http://<hostname>:5000`. The WebSocket URL is derived (`ws://localhost:5000/ws/coop/...`).
2. **Or pass `?api=<url>` on the page URL**, e.g. `http://localhost:8080/?api=http://192.168.1.10:5000` — useful when testing across devices on a LAN.
3. **Otherwise** (any non-localhost host with no override) the production URL wins.

Common dev ports for the WebGL page (`3000`, `5500`, `8000`, `8080`, on both `localhost` and `127.0.0.1`) are pre-allowed in `appsettings.Development.json`'s CORS list. Add more there if you serve from a different port.

Serve a WebGL build with Python's built-in server from the build output directory:

```bash
cd <unity-build-output>
python -m http.server 8080
# open http://localhost:8080/
```

Check the browser console for `[ApiClient] Using API base URL: ...` to confirm the resolver picked up your local server.

## Seeding Test Data

To populate the leaderboard with dummy scores (e.g., for testing rank > 50):

```bash
cd server
node seed-leaderboard.js
```

Creates 60 dummy users with 10x10 scores. Clean up with:

```bash
docker exec arrowthing-dev psql -U postgres -d arrowthing -c \
  "DELETE FROM \"Scores\" WHERE \"UserId\" IN (SELECT \"Id\" FROM \"Users\" WHERE \"Email\" LIKE 'dummy%@test.local'); DELETE FROM \"Users\" WHERE \"Email\" LIKE 'dummy%@test.local';"
```

To wipe all scores (without deleting users):

```bash
docker exec arrowthing-dev psql -U postgres -d arrowthing -c \
  "DELETE FROM \"Scores\";"
```

## API Endpoints

### Auth

| Method | Path | Auth | Description |
|---|---|---|---|
| GET | `/health` | No | Health check |
| POST | `/api/auth/register` | No | Create account (sends verification code) |
| POST | `/api/auth/verify-code` | No | Verify email with 6-digit code |
| POST | `/api/auth/login` | No | Log in (requires verified email) |
| GET | `/api/auth/me` | JWT | Get account info (email, display name) |
| PATCH | `/api/auth/me` | JWT | Update display name |
| POST | `/api/auth/change-password` | JWT | Change password (invalidates sessions) |
| POST | `/api/auth/change-email` | JWT | Request email change (sends 6-digit code) |
| POST | `/api/auth/confirm-email-change` | JWT | Confirm email change via 6-digit code |
| POST | `/api/auth/forgot-password` | No | Request password reset code |
| POST | `/api/auth/reset-password` | No | Reset password via 6-digit code |
| POST | `/api/auth/resend-verification` | No | Resend verification code (5-min cooldown) |
| POST | `/api/admin/lock-account` | Admin key | Lock account + invalidate sessions |
| POST | `/api/admin/unlock-account` | Admin key | Unlock account + send password reset code |

### Scores & Leaderboards

| Method | Path | Auth | Description |
|---|---|---|---|
| POST | `/api/scores` | JWT | Submit replay for verification and scoring |
| GET | `/api/leaderboards/{w}x{h}` | No | Top 50 scores for a board size |
| GET | `/api/leaderboards/all` | No | Top 50 cross-size (biggest board first) |
| GET | `/api/leaderboards/{w}x{h}/me` | JWT | Player's rank and time for a board size |
| GET | `/api/leaderboards/all/me` | JWT | Player's cross-size representative score |
| GET | `/api/replays/{gameId}` | No | Fetch replay JSON (top-50 include snapshot) |

### Example: Register and Verify

```bash
curl -X POST http://localhost:5000/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","password":"password123","displayName":"Test"}'
```

Returns `{"message":"Check your email for a verification code."}` — check inbox or server logs for the code.

```bash
curl -X POST http://localhost:5000/api/auth/verify-code \
  -H "Content-Type: application/json" \
  -d '{"email":"test@example.com","code":"123456"}'
```

Returns `{"token":"...","displayName":"Test"}` on success.

## EF Core Migrations

Migrations live in `server/ArrowThing.Server/Migrations/` and are applied automatically on startup. To create a new migration after changing `AppDbContext` or models:

```bash
cd server
dotnet ef migrations add <MigrationName> --project ArrowThing.Server
```

The connection string must point to PostgreSQL (configured via `.env`) so the snapshot reflects the correct column types.
