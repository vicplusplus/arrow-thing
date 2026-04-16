using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// HTTP client wrapper for the Arrow Thing API.
/// Handles base URL configuration, JWT attachment, and error handling.
/// </summary>
public class ApiClient
{
    private const string DefaultBaseUrl = "https://api.arrow-thing.com";
    private const string LocalBaseUrl = "http://localhost:5000";
    private const string TokenPrefKey = "auth_token";
    private const string DisplayNamePrefKey = "auth_display_name";
    private const string EmailPrefKey = "auth_email";
    private const string DeviceIdPrefKey = "auth_device_id";

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

    public string Token { get; private set; }
    public string DisplayName { get; private set; }
    public string Email { get; private set; }
    public bool IsLoggedIn => !string.IsNullOrEmpty(Token);

    public ApiClient()
    {
#if UNITY_EDITOR
        _baseUrl = LocalBaseUrl;
#else
        _baseUrl = DefaultBaseUrl;
#endif
        // Restore saved session
        Token = PlayerPrefs.GetString(TokenPrefKey, "");
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
            using var request = new UnityWebRequest($"{_baseUrl}/api/auth/me", "PATCH");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = new UnityWebRequest($"{_baseUrl}/api/auth/change-email", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
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
            using var request = new UnityWebRequest($"{_baseUrl}/api/auth/change-password", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
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
            using var request = new UnityWebRequest(
                $"{_baseUrl}/api/auth/confirm-email-change",
                "POST"
            );
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Get($"{_baseUrl}/api/auth/me");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = new UnityWebRequest($"{_baseUrl}/api/scores", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 120;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Get($"{_baseUrl}/api/scores/{gameId}/status");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Get(
                $"{_baseUrl}/api/leaderboards/{width}x{height}/me"
            );
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Get($"{_baseUrl}/api/leaderboards/all/me");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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

    public void Logout()
    {
        Token = "";
        DisplayName = "";
        Email = "";
        PlayerPrefs.DeleteKey(TokenPrefKey);
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

                Token = response.token;
                DisplayName = response.displayName;
                Email = email.Trim().ToLowerInvariant();
                PlayerPrefs.SetString(TokenPrefKey, Token);
                PlayerPrefs.SetString(DisplayNamePrefKey, DisplayName);
                PlayerPrefs.SetString(EmailPrefKey, Email);
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

    public async Task<ApiResult<LobbyResponse>> CreateLobbyAsync(string name)
    {
        var body = JsonUtility.ToJson(new CreateLobbyRequestDto { name = name });
        try
        {
            using var request = new UnityWebRequest($"{_baseUrl}/api/lobbies", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Get($"{_baseUrl}/api/lobbies/me{query}");
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Get(
                $"{_baseUrl}/api/lobbies/{UnityWebRequest.EscapeURL(code)}"
            );
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
            request.timeout = 10;

            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

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
            using var request = UnityWebRequest.Delete($"{_baseUrl}/api/lobbies/{lobbyId}");
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", $"Bearer {Token}");
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
            Debug.LogWarning($"[ApiClient] DeleteLobby failed: {e.Message}");
            return ApiResult<MessageResponse>.Fail(0, "Network error");
        }
    }

    // DTOs (JsonUtility requires serializable classes with public fields)

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
    private class SubmitScoreRequestDto
    {
        public string replayJson;
    }

    [Serializable]
    private class CreateLobbyRequestDto
    {
        public string name;
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
