# TODO — Phase 1B: new-device OTP

Continuation of the codebase improvement pass (see Phase 1 PR #101). This phase
adds a second factor on login for previously-unseen devices.

## Feature

On successful password login, if the client has never completed OTP verification
for this user from this device, the server:

- does **not** issue a JWT;
- emails a 6-digit code (reusing the existing `IEmailService` + `PasswordHasher`
  OTP infrastructure);
- responds with `{ requiresDeviceOtp: true }`.

The client prompts for the code and submits `POST /api/auth/verify-device`
with `{ email, password, code, deviceId }`. On success, the server stores the
device fingerprint (bcrypt hash with work factor 8 — same as other OTPs) and
issues the JWT.

Rollout note: every existing user will hit the OTP challenge on their next
login. That is accepted — no grandfathering.

## Device fingerprint

- Client generates a 256-bit random token on first login and persists it in
  `PlayerPrefs` under key `arrowthing.deviceId`.
- Client sends it on every login as `X-Device-Id` header (not in the JSON body,
  so it's harder to log by accident).
- Server never stores the raw value — only `PasswordHasher.HashOtp(deviceId)`
  keyed on `UserId`. Looking up "does this device belong to this user" is
  `UserDevices.AnyAsync(d => d.UserId == userId && VerifyOtp(deviceId, d.DeviceIdHash))`.
  That's O(N) over the user's device list; acceptable since a user has O(1–10)
  devices.

Design choice: not cookie-based. Cookies would require CORS credentials and CSRF
handling — that's Phase 1C's problem, not this PR's. A localStorage / PlayerPrefs
device ID in a custom header is a standard pattern and works on WebGL today.

## Schema

New table `UserDevices`:

| column | type | notes |
|---|---|---|
| `Id` | `Guid` | PK |
| `UserId` | `Guid` | FK → Users, cascade delete |
| `DeviceIdHash` | `string` | bcrypt(OtpWorkFactor) of raw device id |
| `FirstSeenAt` | `DateTime` | insert timestamp |
| `LastSeenAt` | `DateTime` | updated on every successful login |
| `UserAgent` | `string?` | raw UA for user-facing device list (future) |

Index on `UserId`. New EF Core migration `AddUserDevices`.

## Endpoints

### Modified: `POST /api/auth/login`

Accepts `X-Device-Id` header (required for non-legacy clients; if missing,
treated as "no device match" and OTP is required).

Response shapes:

- Device matches → today's shape `{ token, displayName }` (200).
- Device doesn't match → `{ requiresDeviceOtp: true }` (200). No JWT.
  Email with OTP is sent.
- Wrong password, unverified email, locked account, lockout — unchanged (401/403/429).

Rate limit for the OTP email: reuse the existing 5-minute `EmailCooldown` on
`User.LastVerificationEmailAt`? No — that field is used for the separate
email-verification flow. Add a new field `LastDeviceOtpEmailAt` on `User`.

### New: `POST /api/auth/verify-device`

Body: `{ email, password, code }` + `X-Device-Id` header.

Flow:
1. Re-verify password (defense in depth — the OTP alone shouldn't grant access).
2. Verify code against `User.DeviceOtpCode` / `DeviceOtpCodeExpiresAt`.
3. On success: insert a `UserDevice` row with `DeviceIdHash = HashOtp(deviceId)`,
   clear the OTP fields, issue JWT.
4. Existing failure modes: wrong code 400, expired 400, missing pending 400.

New fields on `User`:
- `DeviceOtpCode` (nullable string)
- `DeviceOtpCodeExpiresAt` (nullable DateTime)
- `LastDeviceOtpEmailAt` (nullable DateTime)

## Client changes (Unity)

- `ApiClient.GetOrCreateDeviceId()` — static helper that reads
  `PlayerPrefs.GetString("arrowthing.deviceId")`, generates a 256-bit random
  token if missing (`System.Security.Cryptography.RandomNumberGenerator`),
  saves, returns.
- `ApiClient.LoginAsync` — add `X-Device-Id` header; handle
  `{ requiresDeviceOtp: true }` response by returning a specific
  `LoginResult.RequiresDeviceOtp` variant.
- `ApiClient.VerifyDeviceAsync(email, password, code)` — new method.
- `AccountManager` — new UI state `DeviceOtpPrompt`: shows "We sent a code to
  X@Y.Z from a new device" + 6-digit input + submit/cancel. On success, the
  existing post-login flow runs.

## Open questions (resolve before implementing)

1. **Max devices per user?** Default: unbounded. No cap for now; add UI + cap
   if it becomes a concern. Agreed?
2. **Device list / revoke UI?** Out of scope for this PR. Data model supports
   it; UI can come later.
3. **Cancel flow?** If the user closes the OTP dialog, do we discard the
   pending code? Simplest: leave it. They can just try again — the code
   expires in 10 min.
4. **Should `X-Device-Id` be *required* for login?** Yes for the current Unity
   client. Clients that don't send it get treated as "no device match" and
   always hit OTP. A malicious bot that omits the header hits email-OTP
   rate-limiting immediately.
5. **Grandfather existing sessions?** No. Only new logins trigger the OTP.
   Already-issued JWTs continue to work until expiry (30 days) or until the
   user's security stamp is rotated.

## Testing plan

### Automated (xUnit, integration)

- `Login_NoDeviceId_RequiresOtp` — POST /login with correct password, no
  `X-Device-Id` → 200 with `requiresDeviceOtp: true`, email captured, no token.
- `Login_UnknownDeviceId_RequiresOtp` — with a fresh device id, same outcome.
- `VerifyDevice_ValidCode_IssuesTokenAndStoresDevice` — POST /verify-device
  with correct code → 200 with token; a second login with same device id
  skips OTP.
- `VerifyDevice_WrongCode_Returns400_NoDeviceStored`.
- `VerifyDevice_ExpiredCode_Returns400`.
- `VerifyDevice_WrongPassword_Returns401` — even with valid OTP.
- `Login_KnownDeviceSkipsOtp` — after a verify, re-logging in skips OTP and
  bumps `LastSeenAt`.
- `Login_OtpRateLimit` — two OTP requests within 5 minutes: the second returns
  429.

Unit tests:
- `UserDevice` column mapping + unique index.

### Manual

- Fresh install (no PlayerPrefs) → login → OTP prompt → enter code → in.
  Re-open app → login → no prompt.
- Clear PlayerPrefs → login → new device OTP.
- Wrong code 3x → still able to retry after the server's OTP window resets.

## Out of scope (deferred to Phase 1C)

- HttpOnly cookie JWT / in-memory access token / silent refresh. Cookie-based
  auth brings CORS-credentials and CSRF requirements that don't belong in this
  PR.

## Definition of done

- Migration applied cleanly against existing prod DB (idempotent).
- All new tests pass.
- Existing auth tests still pass (login flow unchanged for JWTs on a known
  device).
- Manual test cases above executed and recorded below.
