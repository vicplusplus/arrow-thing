# Networking

How the Unity client picks an API base URL and a WebSocket base URL for
each of the four environments we ship into. Resolution lives in three
places — this doc is the index, the code is what actually runs.

## Environments

| # | Where the client runs                | API base URL                       | WS base URL                        |
| - | ------------------------------------ | ---------------------------------- | ---------------------------------- |
| 1 | Unity Editor + local server          | `http://localhost:5000`            | `ws://localhost:5000`              |
| 2 | Local WebGL build + local server     | `http://localhost:5000`            | `ws://localhost:5000`              |
| 3 | Staging WebGL (staging.arrow-thing)  | `https://staging.arrow-thing.com`* | `wss://api-staging.arrow-thing.com`|
| 4 | Production WebGL (arrow-thing.com)   | `https://api.arrow-thing.com`      | `wss://api.arrow-thing.com`        |

\* Pages Functions on the staging site reverse-proxy `/api/*` to
`api-staging.arrow-thing.com`. Routing through the page origin keeps the
auth cookies (`arrow_access`, `arrow_refresh`) same-origin, which the
browser otherwise strips on cross-origin XHR. WebSockets bypass the
proxy because Pages Functions don't proxy WS upgrades cleanly; the
WS handshake passes the JWT in the query string instead, so cookie scope
doesn't matter for it.

## Resolution

Two cooperating mechanisms decide the URL at `ApiClient` construction
time. The first one to return a non-empty value wins.

### 1. WebGL JS resolver — [`Assets/Plugins/WebGL/ApiUrlOverride.jslib`](../Assets/Plugins/WebGL/ApiUrlOverride.jslib)

Only runs in WebGL builds. Inspects the page that loaded the build:

| Page hostname                     | Returned API URL                | Notes                          |
| --------------------------------- | ------------------------------- | ------------------------------ |
| `?api=<url>` query param present  | `<url>` (trailing slash stripped) | Full override; mirrors to WS  |
| `localhost` / `127.0.0.1` / `0.0.0.0` | `http://<host>:5000`        | Env 2                          |
| `staging.arrow-thing.com`         | `window.location.origin`        | Env 3                          |
| anything else                     | empty string                    | Falls through to mechanism #2  |

`ApiWsUrl_Resolve` mirrors the same logic but emits `ws://` / `wss://`
URLs and points staging directly at `api-staging.arrow-thing.com` (the
proxy bypass).

### 2. JSON config — [`Assets/Resources/BackendConfig.json`](../Assets/Resources/BackendConfig.json)

Used by Editor and any non-WebGL build, and as the fallback when the
WebGL resolver returns empty (env 4):

```json
{
  "editorApiBaseUrl":  "http://localhost:5000",
  "runtimeApiBaseUrl": "https://api.arrow-thing.com"
}
```

Loaded by [`BackendConfig.cs`](../Assets/Scripts/View/Account/BackendConfig.cs).
Picks `editorApiBaseUrl` under `#if UNITY_EDITOR`, otherwise
`runtimeApiBaseUrl`. Falls back to compile-time defaults (same values)
if the JSON is missing or malformed, so a misdeployed build still boots.

The WS URL is derived by scheme-swapping the resolved API URL
(`ApiClient.SchemeSwapToWs`) — no separate config needed.

## How each environment is selected

| # | Selection mechanism                                                  |
| - | -------------------------------------------------------------------- |
| 1 | `#if UNITY_EDITOR` → `editorApiBaseUrl` from BackendConfig           |
| 2 | jslib detects localhost hostname → returns local URL                 |
| 3 | jslib detects `staging.arrow-thing.com` → returns page origin        |
| 4 | jslib returns empty → C# falls back to `runtimeApiBaseUrl`           |

To point Editor (env 1) at staging or production for QA, edit
`BackendConfig.json` before pressing Play. To point a local WebGL build
(env 2) at a non-default backend, append `?api=https://...` to the page
URL.

## Files

- [`ApiClient.cs`](../Assets/Scripts/View/Account/ApiClient.cs) — calls into both resolvers, merges results, exposes `BaseUrl` / `BaseWsUrl`.
- [`BackendConfig.cs`](../Assets/Scripts/View/Account/BackendConfig.cs) — JSON loader, caching, fallback defaults.
- [`BackendConfig.json`](../Assets/Resources/BackendConfig.json) — the values themselves; build pipelines can swap this per env.
- [`ApiUrlOverride.jslib`](../Assets/Plugins/WebGL/ApiUrlOverride.jslib) — WebGL hostname-based resolver.
- [`CookieAuth.jslib`](../Assets/Plugins/WebGL/CookieAuth.jslib) — patches XHR + fetch on the resolved API origin so HttpOnly auth cookies are sent.
