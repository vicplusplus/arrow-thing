using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// HTTP client wrapper for the Arrow Thing API.
///
/// Auth model:
/// - <b>WebGL builds</b> use HttpOnly cookies (<c>arrow_access</c> +
///   <c>arrow_refresh</c>). The browser manages them; the
///   <c>CookieAuth.jslib</c> plugin patches XHR to send credentials on the
///   API origin. The bearer header isn't set.
/// - <b>Editor / other builds</b> fall back to bearer tokens persisted in
///   <c>PlayerPrefs</c>. The server accepts either, so both paths work
///   against the same backend.
/// </summary>
public class ApiClient
{
    private const string DefaultBaseUrl = "https://api.arrow-thing.com";
    private const string LocalBaseUrl = "http://localhost:5000";
    private const string TokenPrefKey = "auth_token";
    private const string RefreshTokenPrefKey = "auth_refresh_token";
    private const string DisplayNamePrefKey = "auth_display_name";
    private const string EmailPrefKey = "auth_email";
    private const string DeviceIdPrefKey = "auth_device_id";

#if UNITY_WEBGL && !UNITY_EDITOR
    private const bool UseCookieAuth = true;

    [DllImport("__Internal")]
    private static extern void EnableCredentialsForApi(string apiOrigin);

    [DllImport("__Internal")]
    private static extern string ApiUrl_Resolve();
#else
    private const bool UseCookieAuth = false;
#endif

    private static bool _credentialsEnabled;

    /// <summary>
    /// Stable random identifier for this install. Sent as <c>X-Device-Id</c>
    /// on auth requests so the server can skip the email OTP challenge after
    /// the first successful verification. Generated on first access and
    /// persisted in PlayerPrefs. Clearing PlayerPrefs forces a fresh device
    /// verification on next login.
    /// </summary>
    public static string GetOrCreateDeviceId()
    {
        var existing = PlayerPrefs.GetString(DeviceIdPrefKey, "");
        if (!string.IsNullOrEmpty(existing))
            return existing;

        var bytes = new byte[32];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        var id = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        PlayerPrefs.SetString(DeviceIdPrefKey, id);
        return id;
    }

    private readonly string _baseUrl;

    // Defaulted to "" so WebGL cookie mode (which never writes to Token /
    // RefreshToken) doesn't hand null to Uri.EscapeDataString in the WS
    // connect URL — that threw before new WebSocket() could fire, which is
    // why the handshake never even hit the network.
    public string Token { get; private set; } = "";
    public string RefreshToken { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string Email { get; private set; } = "";

    // In cookie mode the tokens live in the browser (HttpOnly) so JS — and
    // therefore this client — can't see them. We proxy "logged in" off the
    // presence of DisplayName, which we persist after a successful login.
    // A stale DisplayName just means the first authenticated request returns
    // 401 and the client clears local state.
    public bool IsLoggedIn =>
        UseCookieAuth ? !string.IsNullOrEmpty(DisplayName) : !string.IsNullOrEmpty(Token);

    /// <summary>
    /// WebSocket-scheme version of the base URL (ws:// or wss://).
    /// Used by CoopClient for lobby connections.
    /// </summary>
    public string BaseWsUrl
    {
        get
        {
            if (_baseUrl.StartsWith("https://"))
                return "wss://" + _baseUrl.Substring("https://".Length);
            if (_baseUrl.StartsWith("http://"))
                return "ws://" + _baseUrl.Substring("http://".Length);
            return _baseUrl;
        }
    }

    public ApiClient()
    {
#if UNITY_EDITOR
        _baseUrl = LocalBaseUrl;
#else
        _baseUrl = DefaultBaseUrl;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
        // Allow the hosting page to override the API URL — either via a
        // `?api=<url>` query param or automatically when served from
        // localhost. Lets a local WebGL build talk to a local API without
        // rebuilding.
        try
        {
            var resolved = ApiUrl_Resolve();
            if (!string.IsNullOrEmpty(resolved))
                _baseUrl = resolved;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] ApiUrl_Resolve failed: {e.Message}");
        }
        Debug.Log($"[ApiClient] Using API base URL: {_baseUrl}");

        // Patch XHR so the browser attaches the arrow_access / arrow_refresh
        // cookies on API calls. Idempotent on the JS side but we also guard
        // here so we don't invoke the P/Invoke more than once.
        if (!_credentialsEnabled)
        {
            try
            {
                EnableCredentialsForApi(_baseUrl);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ApiClient] Failed to enable credentials: {e.Message}");
            }
            _credentialsEnabled = true;
        }
#endif

        // Cookie mode persists nothing about the token itself — the browser
        // holds it. We still track the displayName / email locally so the UI
        // can render before the first /me call.
#if !UNITY_WEBGL || UNITY_EDITOR
        Token = PlayerPrefs.GetString(TokenPrefKey, "");
        RefreshToken = PlayerPrefs.GetString(RefreshTokenPrefKey, "");
#endif
        DisplayName = PlayerPrefs.GetString(DisplayNamePrefKey, "");
        Email = PlayerPrefs.GetString(EmailPrefKey, "");
    }

    /// <summary>
    /// Checks server connectivity by hitting the health endpoint.
    /// Returns true if the server responds with 200.
    /// </summary>
    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            using var request = UnityWebRequest.Get($"{_baseUrl}/health");
            request.timeout = 5;
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
            return request.result == UnityWebRequest.Result.Success;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] Health check failed: {e.Message}");
            return false;
        }
    }

    public async Task<ApiResult<MessageResponse>> RegisterAsync(
        string email,
        string password,
        string displayName
    )
    {
        var body = JsonUtility.ToJson(
            new RegisterRequest
            {
                email = email,
                password = password,
                displayName = displayName,
            }
        );
        return await PostMessageAsync("/api/auth/register", body);
    }

    public async Task<ApiResult<AuthResponse>> VerifyCodeAsync(string email, string code)
    {
        var body = JsonUtility.ToJson(new VerifyCodeRequestDto { email = email, code = code });
        return await PostAuthAsync("/api/auth/verify-code", body, email);
    }

    public async Task<ApiResult<AuthResponse>> LoginAsync(string email, string password)
    {
        var body = JsonUtility.ToJson(new LoginRequest { email = email, password = password });
        return await PostAuthAsync("/api/auth/login", body, email);
    }

    public async Task<ApiResult<DisplayNameResponse>> UpdateDisplayNameAsync(string displayName)
    {
        var body = JsonUtility.ToJson(new UpdateDisplayNameRequest { displayName = displayName });

        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/auth/me", "PATCH");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<DisplayNameResponse>(
                    request.downloadHandler.text
                );
                DisplayName = response.displayName;
                PlayerPrefs.SetString(DisplayNamePrefKey, DisplayName);
                return ApiResult<DisplayNameResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<DisplayNameResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] UpdateDisplayName failed: {e.Message}");
            return ApiResult<DisplayNameResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MessageResponse>> ForgotPasswordAsync(string email)
    {
        var body = JsonUtility.ToJson(new ForgotPasswordRequestDto { email = email });
        return await PostMessageAsync("/api/auth/forgot-password", body);
    }

    public async Task<ApiResult<MessageResponse>> ResetPasswordAsync(
        string email,
        string code,
        string newPassword
    )
    {
        var body = JsonUtility.ToJson(
            new ResetPasswordRequestDto
            {
                email = email,
                code = code,
                newPassword = newPassword,
            }
        );
        return await PostMessageAsync("/api/auth/reset-password", body);
    }

    public async Task<ApiResult<MessageResponse>> ResendVerificationAsync(string email)
    {
        var body = JsonUtility.ToJson(new ResendVerificationRequestDto { email = email });
        return await PostMessageAsync("/api/auth/resend-verification", body);
    }

    public async Task<ApiResult<MessageResponse>> ChangeEmailAsync(
        string newEmail,
        string currentPassword
    )
    {
        var body = JsonUtility.ToJson(
            new ChangeEmailRequestDto { newEmail = newEmail, currentPassword = currentPassword }
        );

        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/auth/change-email", "POST");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] ChangeEmail failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MessageResponse>> ChangePasswordAsync(
        string currentPassword,
        string newPassword
    )
    {
        var body = JsonUtility.ToJson(
            new ChangePasswordRequestDto
            {
                currentPassword = currentPassword,
                newPassword = newPassword,
            }
        );

        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/auth/change-password", "POST");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] ChangePassword failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MessageResponse>> ConfirmEmailChangeAsync(
        string newEmail,
        string code
    )
    {
        var body = JsonUtility.ToJson(
            new ConfirmEmailChangeRequestDto { email = newEmail, code = code }
        );

        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/auth/confirm-email-change", "POST");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                Email = newEmail.Trim().ToLowerInvariant();
                PlayerPrefs.SetString(EmailPrefKey, Email);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] ConfirmEmailChange failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MeResponse>> GetMeAsync()
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Get($"{_baseUrl}/api/auth/me");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MeResponse>(request.downloadHandler.text);
                DisplayName = response.displayName;
                Email = response.email;
                PlayerPrefs.SetString(DisplayNamePrefKey, DisplayName);
                PlayerPrefs.SetString(EmailPrefKey, Email);
                return ApiResult<MeResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MeResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetMe failed: {e.Message}");
            return ApiResult<MeResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<SubmitResultResponse>> SubmitScoreAsync(string replayJson)
    {
        var body = JsonUtility.ToJson(new SubmitScoreRequestDto { replayJson = replayJson });
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/scores", "POST");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 120;
                return r;
            });

            // 202 Accepted — async verification, need to poll
            if (request.responseCode == 202)
            {
                var accepted = JsonUtility.FromJson<SubmitAcceptedResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<SubmitResultResponse>.Accepted(accepted.gameId);
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<SubmitResultResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<SubmitResultResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<SubmitResultResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] SubmitScore failed: {e.Message}");
            return ApiResult<SubmitResultResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<ScoreStatusResponse>> GetScoreStatusAsync(string gameId)
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Get($"{_baseUrl}/api/scores/{gameId}/status");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<ScoreStatusResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<ScoreStatusResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<ScoreStatusResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetScoreStatus failed: {e.Message}");
            return ApiResult<ScoreStatusResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<GlobalLeaderboardResponse>> GetLeaderboardAsync(
        int width,
        int height
    )
    {
        try
        {
            using var request = UnityWebRequest.Get(
                $"{_baseUrl}/api/leaderboards/{width}x{height}"
            );
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<GlobalLeaderboardResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<GlobalLeaderboardResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<GlobalLeaderboardResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetLeaderboard failed: {e.Message}");
            return ApiResult<GlobalLeaderboardResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<GlobalLeaderboardResponse>> GetLeaderboardAllAsync()
    {
        try
        {
            using var request = UnityWebRequest.Get($"{_baseUrl}/api/leaderboards/all");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<GlobalLeaderboardResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<GlobalLeaderboardResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<GlobalLeaderboardResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetLeaderboardAll failed: {e.Message}");
            return ApiResult<GlobalLeaderboardResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<PlayerEntryResponse>> GetPlayerEntryAsync(int width, int height)
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Get($"{_baseUrl}/api/leaderboards/{width}x{height}/me");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<PlayerEntryResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<PlayerEntryResponse>.Ok(response);
            }

            // 404 = no score, not an error
            if (request.responseCode == 404)
                return ApiResult<PlayerEntryResponse>.Fail(404, null);

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<PlayerEntryResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetPlayerEntry failed: {e.Message}");
            return ApiResult<PlayerEntryResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<PlayerEntryResponse>> GetPlayerEntryAllAsync()
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Get($"{_baseUrl}/api/leaderboards/all/me");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<PlayerEntryResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<PlayerEntryResponse>.Ok(response);
            }

            if (request.responseCode == 404)
                return ApiResult<PlayerEntryResponse>.Fail(404, null);

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<PlayerEntryResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetPlayerEntryAll failed: {e.Message}");
            return ApiResult<PlayerEntryResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<ReplayFetchResponse>> GetReplayAsync(string gameId)
    {
        try
        {
            using var request = UnityWebRequest.Get($"{_baseUrl}/api/replays/{gameId}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<ReplayFetchResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<ReplayFetchResponse>.Ok(response);
            }

            if (request.responseCode == 404)
                return ApiResult<ReplayFetchResponse>.Fail(404, null);

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<ReplayFetchResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetReplay failed: {e.Message}");
            return ApiResult<ReplayFetchResponse>.Fail(0, "Network error");
        }
    }

    /// <summary>
    /// Fetches the v6 co-op replay for the given lobby. Caller must be
    /// registered in the lobby; 403 on non-participants, 404 on missing
    /// lobby, 410 on stripped snapshot.
    /// </summary>
    public async Task<ApiResult<ReplayData>> GetLobbyReplayAsync(string code)
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
                UnityWebRequest.Get($"{_baseUrl}/api/lobbies/{code}/replay")
            );
            if (request.result == UnityWebRequest.Result.Success)
            {
                var body = request.downloadHandler.text;
                var replay = Newtonsoft.Json.JsonConvert.DeserializeObject<ReplayData>(body);
                return ApiResult<ReplayData>.Ok(replay);
            }
            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<ReplayData>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetLobbyReplay failed: {e.Message}");
            return ApiResult<ReplayData>.Fail(0, "Network error");
        }
    }

    /// <summary>
    /// Logs out locally. Best-effort fires <c>POST /api/auth/logout</c> to
    /// revoke the refresh token server-side; even if that call fails (offline,
    /// 5xx) the local session is still cleared so the user can sign back in.
    /// </summary>
    public async Task LogoutAsync()
    {
        // Snapshot the refresh token (bearer mode only) BEFORE clearing — we
        // still want to revoke it server-side even though the local session
        // is gone.
        var refresh = RefreshToken;
        ClearSessionLocal();
        if (!UseCookieAuth && string.IsNullOrEmpty(refresh))
            return;
        try
        {
            var body = JsonUtility.ToJson(
                new LogoutRequestDto { refreshToken = UseCookieAuth ? "" : refresh }
            );
            using var request = new UnityWebRequest($"{_baseUrl}/api/auth/logout", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 5;
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] Logout server call failed: {e.Message}");
        }
    }

    /// <summary>
    /// Clears local credentials without contacting the server. Use this when
    /// you've already detected the session is invalid (e.g. transparent
    /// refresh failed). Prefer <see cref="LogoutAsync"/> for user-initiated
    /// logout so the refresh token is revoked server-side too.
    /// </summary>
    public void Logout()
    {
        ClearSessionLocal();
    }

    /// <summary>
    /// Exchanges the stored refresh token for a fresh access + refresh pair.
    /// On success, the new tokens are persisted. On failure (401 / network),
    /// the local session is cleared and the caller should treat the user as
    /// logged out.
    /// </summary>
    public async Task<bool> RefreshAsync()
    {
        return await RefreshInternalAsync();
    }

    /// <summary>
    /// Refreshes the access token before an operation that can't use the
    /// `SendAuthenticatedAsync` retry-on-401 pattern — specifically the co-op
    /// WebSocket upgrade, which carries auth in the URL / cookie and has no
    /// Bearer retry path. Called immediately before `ConnectAsync` /
    /// `ReconnectAsync`: in cookie mode the browser picks up the freshly-issued
    /// `arrow_access`; in bearer mode the caller reads `Token` after it returns.
    /// No-op (returns true) when the caller isn't logged in at all.
    /// </summary>
    public async Task<bool> EnsureFreshTokenAsync()
    {
        if (!CanAttemptRefresh())
            return false;
        return await RefreshInternalAsync();
    }

    private async Task<bool> RefreshInternalAsync()
    {
        if (!CanAttemptRefresh())
            return false;

        try
        {
            // Cookie mode sends an empty body — the server reads the refresh
            // value from the arrow_refresh cookie. Bearer mode sends the
            // token from PlayerPrefs.
            var body = JsonUtility.ToJson(
                new RefreshRequestDto { refreshToken = UseCookieAuth ? "" : RefreshToken }
            );
            using var request = new UnityWebRequest($"{_baseUrl}/api/auth/refresh", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                if (request.responseCode == 401 || request.responseCode == 403)
                    ClearSessionLocal();
                return false;
            }

            var response = JsonUtility.FromJson<AuthResponse>(request.downloadHandler.text);
            StoreSession(response, Email);
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] Refresh failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sends a request that requires the access JWT, transparently refreshing
    /// once on 401 and retrying. Use this for any authenticated endpoint
    /// that runs after the initial login flow — without it, an expired access
    /// token boots the user out instead of silently rolling forward.
    ///
    /// The factory is invoked again to build the retry request because
    /// <see cref="UnityWebRequest"/> can only be sent once.
    /// </summary>
    private async Task<UnityWebRequest> SendAuthenticatedAsync(Func<UnityWebRequest> requestFactory)
    {
        var request = requestFactory();
        AttachAuth(request);
        var op = request.SendWebRequest();
        while (!op.isDone)
            await Task.Yield();

        // In cookie mode, an "authenticated" request always has the cookie
        // attached by the browser; we still try one refresh on 401. In bearer
        // mode the refresh token in PlayerPrefs gates the retry.
        if (request.responseCode != 401 || !CanAttemptRefresh())
            return request;

        if (!await RefreshAsync())
            return request;

        request.Dispose();
        request = requestFactory();
        AttachAuth(request);
        op = request.SendWebRequest();
        while (!op.isDone)
            await Task.Yield();
        return request;
    }

    private void AttachAuth(UnityWebRequest request)
    {
        // Cookie-mode clients rely on the browser attaching arrow_access
        // automatically via withCredentials; adding a Bearer header here
        // would be a no-op since Token is empty.
        if (!UseCookieAuth && !string.IsNullOrEmpty(Token))
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
    }

    private bool CanAttemptRefresh() => UseCookieAuth || !string.IsNullOrEmpty(RefreshToken);

    private void StoreSession(AuthResponse response, string email)
    {
        // In cookie mode the browser owns the tokens; don't mirror them to
        // PlayerPrefs (that would defeat the point of HttpOnly).
        if (!UseCookieAuth)
        {
            Token = response.token;
            RefreshToken = response.refreshToken ?? "";
            PlayerPrefs.SetString(TokenPrefKey, Token);
            PlayerPrefs.SetString(RefreshTokenPrefKey, RefreshToken);
        }
        DisplayName = response.displayName;
        Email = email.Trim().ToLowerInvariant();
        PlayerPrefs.SetString(DisplayNamePrefKey, DisplayName);
        PlayerPrefs.SetString(EmailPrefKey, Email);
    }

    private void ClearSessionLocal()
    {
        Token = "";
        RefreshToken = "";
        DisplayName = "";
        Email = "";
        PlayerPrefs.DeleteKey(TokenPrefKey);
        PlayerPrefs.DeleteKey(RefreshTokenPrefKey);
        PlayerPrefs.DeleteKey(DisplayNamePrefKey);
        PlayerPrefs.DeleteKey(EmailPrefKey);
    }

    private async Task<ApiResult<AuthResponse>> PostAuthAsync(
        string path,
        string jsonBody,
        string email
    )
    {
        try
        {
            using var request = new UnityWebRequest($"{_baseUrl}{path}", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-Device-Id", GetOrCreateDeviceId());
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<AuthResponse>(request.downloadHandler.text);

                // Device-OTP pending — don't save any credentials. Caller prompts
                // the user for the 6-digit email code then calls VerifyDeviceAsync.
                if (response.requiresDeviceOtp)
                    return ApiResult<AuthResponse>.Ok(response);

                StoreSession(response, email);
                return ApiResult<AuthResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<AuthResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] Auth request failed: {e.Message}");
            return ApiResult<AuthResponse>.Fail(0, "Network error");
        }
    }

    /// <summary>
    /// Completes the new-device email OTP challenge. Call after
    /// <see cref="LoginAsync"/> returned <c>requiresDeviceOtp = true</c>.
    /// On success, persists the JWT and marks this device as trusted.
    /// </summary>
    public async Task<ApiResult<AuthResponse>> VerifyDeviceAsync(
        string email,
        string password,
        string code
    )
    {
        var body = JsonUtility.ToJson(
            new VerifyDeviceRequestDto
            {
                email = email,
                password = password,
                code = code,
            }
        );
        return await PostAuthAsync("/api/auth/verify-device", body, email);
    }

    private async Task<ApiResult<MessageResponse>> PostMessageAsync(string path, string jsonBody)
    {
        try
        {
            using var request = new UnityWebRequest($"{_baseUrl}{path}", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] Request failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    private static string TryParseError(string responseBody)
    {
        if (string.IsNullOrEmpty(responseBody))
            return "Unknown error";
        try
        {
            var err = JsonUtility.FromJson<ErrorResponse>(responseBody);
            return string.IsNullOrEmpty(err.error) ? "Unknown error" : err.error;
        }
        catch
        {
            return "Unknown error";
        }
    }

    // -- Co-op lobbies (Phase 3) ---------------------------------------------

    public async Task<ApiResult<LobbyResponse>> CreateLobbyAsync(string name, int width, int height)
    {
        var body = JsonUtility.ToJson(
            new CreateLobbyRequestDto
            {
                name = name,
                width = width,
                height = height,
            }
        );
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/lobbies", "POST");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success || request.responseCode == 201)
            {
                var response = JsonUtility.FromJson<LobbyResponse>(request.downloadHandler.text);
                return ApiResult<LobbyResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<LobbyResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] CreateLobby failed: {e.Message}");
            return ApiResult<LobbyResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<LobbyListResponse>> ListMyLobbiesAsync(
        string filter = null,
        string sort = null,
        int page = 0
    )
    {
        var query = $"?page={page}";
        if (!string.IsNullOrEmpty(filter))
            query += $"&filter={UnityWebRequest.EscapeURL(filter)}";
        if (!string.IsNullOrEmpty(sort))
            query += $"&sort={UnityWebRequest.EscapeURL(sort)}";

        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Get($"{_baseUrl}/api/lobbies/me{query}");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<LobbyListResponse>(
                    request.downloadHandler.text
                );
                return ApiResult<LobbyListResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<LobbyListResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] ListMyLobbies failed: {e.Message}");
            return ApiResult<LobbyListResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<LobbyResponse>> GetLobbyAsync(string code)
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Get(
                    $"{_baseUrl}/api/lobbies/{UnityWebRequest.EscapeURL(code)}"
                );
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<LobbyResponse>(request.downloadHandler.text);
                return ApiResult<LobbyResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<LobbyResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] GetLobby failed: {e.Message}");
            return ApiResult<LobbyResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MessageResponse>> DeleteLobbyAsync(string lobbyId)
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = UnityWebRequest.Delete($"{_baseUrl}/api/lobbies/{lobbyId}");
                r.downloadHandler = new DownloadHandlerBuffer();
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] DeleteLobby failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    // -- Co-op color (Phase 5) ---------------------------------------------------

    public async Task<ApiResult<CoopColorResponseDto>> UpdateCoopColorAsync(string hex)
    {
        var body = JsonUtility.ToJson(new UpdateCoopColorRequestDto { coopColor = hex });
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/auth/me/coop-color", "PATCH");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<CoopColorResponseDto>(
                    request.downloadHandler.text
                );
                return ApiResult<CoopColorResponseDto>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<CoopColorResponseDto>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] UpdateCoopColor failed: {e.Message}");
            return ApiResult<CoopColorResponseDto>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MessageResponse>> RetryGenerationAsync(string lobbyId)
    {
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/lobbies/{lobbyId}/retry-gen", "POST");
                r.uploadHandler = new UploadHandlerRaw(new byte[0]);
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] RetryGeneration failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    public async Task<ApiResult<MessageResponse>> RenameLobbyAsync(string lobbyId, string name)
    {
        var body = JsonUtility.ToJson(new RenameLobbyRequestDto { name = name });
        try
        {
            using var request = await SendAuthenticatedAsync(() =>
            {
                var r = new UnityWebRequest($"{_baseUrl}/api/lobbies/{lobbyId}", "PATCH");
                r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                r.downloadHandler = new DownloadHandlerBuffer();
                r.SetRequestHeader("Content-Type", "application/json");
                r.timeout = 10;
                return r;
            });

            if (request.result == UnityWebRequest.Result.Success)
            {
                var response = JsonUtility.FromJson<MessageResponse>(request.downloadHandler.text);
                return ApiResult<MessageResponse>.Ok(response);
            }

            var error = TryParseError(request.downloadHandler.text);
            return ApiResult<MessageResponse>.Fail(request.responseCode, error);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ApiClient] RenameLobby failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    // DTOs (JsonUtility requires serializable classes with public fields)

    [Serializable]
    private class RenameLobbyRequestDto
    {
        public string name;
    }

    [Serializable]
    private class RegisterRequest
    {
        public string email;
        public string password;
        public string displayName;
    }

    [Serializable]
    private class VerifyCodeRequestDto
    {
        public string email;
        public string code;
    }

    [Serializable]
    private class LoginRequest
    {
        public string email;
        public string password;
    }

    [Serializable]
    private class UpdateDisplayNameRequest
    {
        public string displayName;
    }

    [Serializable]
    private class ForgotPasswordRequestDto
    {
        public string email;
    }

    [Serializable]
    private class ResendVerificationRequestDto
    {
        public string email;
    }

    [Serializable]
    private class ResetPasswordRequestDto
    {
        public string email;
        public string code;
        public string newPassword;
    }

    [Serializable]
    private class ChangeEmailRequestDto
    {
        public string newEmail;
        public string currentPassword;
    }

    [Serializable]
    private class ChangePasswordRequestDto
    {
        public string currentPassword;
        public string newPassword;
    }

    [Serializable]
    private class ConfirmEmailChangeRequestDto
    {
        public string email;
        public string code;
    }

    [Serializable]
    private class VerifyDeviceRequestDto
    {
        public string email;
        public string password;
        public string code;
    }

    [Serializable]
    private class RefreshRequestDto
    {
        public string refreshToken;
    }

    [Serializable]
    private class LogoutRequestDto
    {
        public string refreshToken;
    }

    [Serializable]
    private class SubmitScoreRequestDto
    {
        public string replayJson;
    }

    [Serializable]
    private class CreateLobbyRequestDto
    {
        public string name;
        public int width;
        public int height;
    }

    [Serializable]
    private class UpdateCoopColorRequestDto
    {
        public string coopColor;
    }

    [Serializable]
    private class ErrorResponse
    {
        public string error;
    }
}

[Serializable]
public class AuthResponse
{
    public string token;
    public string displayName;
    public string refreshToken;
    public int expiresIn;
    public int refreshExpiresIn;

    /// <summary>
    /// Set by the server (as the only field on the response) when a login
    /// from an unrecognized device must be confirmed via email OTP before a
    /// JWT will be issued. When true, <see cref="token"/> is empty.
    /// </summary>
    public bool requiresDeviceOtp;
}

[Serializable]
public class DisplayNameResponse
{
    public string displayName;
}

[Serializable]
public class MessageResponse
{
    public string message;
}

[Serializable]
public class MeResponse
{
    public string email;
    public string displayName;
    public string coopColor;
}

[Serializable]
public class SubmitResultResponse
{
    public bool verified;
    public int rank;
    public bool isPersonalBest;
    public string reason;
}

[Serializable]
public class SubmitAcceptedResponse
{
    public string gameId;
    public string status;
}

[Serializable]
public class ScoreStatusResponse
{
    public string status;
    public int rank;
    public bool isPersonalBest;
    public string reason;
}

[Serializable]
public class GlobalLeaderboardResponse
{
    public int totalEntries;
    public GlobalLeaderboardEntry[] entries;
}

[Serializable]
public class GlobalLeaderboardEntry
{
    public int rank;
    public string displayName;
    public double time;
    public string gameId;
    public int boardWidth;
    public int boardHeight;
}

[Serializable]
public class PlayerEntryResponse
{
    public int rank;
    public int totalEntries;
    public double time;
    public string gameId;
    public int boardWidth;
    public int boardHeight;
    public bool flagged;
}

[Serializable]
public class ReplayFetchResponse
{
    public string replayJson;
}

[Serializable]
public class CoopColorResponseDto
{
    public string coopColor;
}

[Serializable]
public class LobbyResponse
{
    public string id;
    public string code;
    public string name;
    public string ownerUserId;
    public string ownerDisplayName;
    public int width;
    public int height;
    public short status;
    public string createdAt;
    public string lastActivityAt;
    public string shareUrl;
}

[Serializable]
public class LobbyListEntryDto
{
    public string id;
    public string code;
    public string name;
    public string ownerDisplayName;
    public int width;
    public int height;
    public short status;
    public string createdAt;
    public string lastActivityAt;
    public bool youAreOwner;
    public int yourClearCount;
    public string shareUrl;
}

[Serializable]
public class LobbyListResponse
{
    public LobbyListEntryDto[] entries;
    public int page;
    public bool hasMore;
}

public class ApiResult<T>
{
    public bool Success { get; private set; }
    public bool IsAccepted { get; private set; }
    public string AcceptedGameId { get; private set; }
    public T Data { get; private set; }
    public long StatusCode { get; private set; }
    public string Error { get; private set; }

    public static ApiResult<T> Ok(T data) => new ApiResult<T> { Success = true, Data = data };

    public static ApiResult<T> Accepted(string gameId) =>
        new ApiResult<T>
        {
            Success = true,
            IsAccepted = true,
            AcceptedGameId = gameId,
            StatusCode = 202,
        };

    public static ApiResult<T> Fail(long statusCode, string error) =>
        new ApiResult<T>
        {
            Success = false,
            StatusCode = statusCode,
            Error = error,
        };
}
