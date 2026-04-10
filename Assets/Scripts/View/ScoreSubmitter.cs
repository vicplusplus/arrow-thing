using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Static helper for submitting scores to the global leaderboard.
/// Fire-and-forget: runs entirely in the background, shows results
/// via <see cref="GlobalToast"/>. No caller needs to await the result.
/// </summary>
public static class ScoreSubmitter
{
    private const int PollAttempts = 3;
    private const float PollIntervalSeconds = 2f;

    /// <summary>
    /// Submits a completed replay to the server in the background.
    /// Shows success/error/pending toasts via GlobalToast.
    /// This method returns immediately — do not await.
    /// </summary>
    public static void Submit(ReplayData replay)
    {
        var api = new ApiClient();
        if (!api.IsLoggedIn)
            return;

        var replayJson = JsonConvert.SerializeObject(replay);
        SubmitInBackground(api, replayJson);
    }

    private static async void SubmitInBackground(ApiClient api, string replayJson)
    {
        try
        {
            var result = await api.SubmitScoreAsync(replayJson);

            if (!result.Success)
            {
                string message = DescribeError(result.StatusCode, result.Error);
                Debug.LogWarning(
                    $"[ScoreSubmitter] Submission failed ({result.StatusCode}): {message}"
                );
                ShowError(message);
                return;
            }

            // 202 Accepted — poll for verification result
            if (result.IsAccepted)
            {
                var gameId = result.AcceptedGameId;
                Debug.Log(
                    $"[ScoreSubmitter] Score accepted for verification (gameId={gameId}), polling..."
                );

                for (int i = 0; i < PollAttempts; i++)
                {
                    await Task.Delay((int)(PollIntervalSeconds * 1000));

                    var statusResult = await api.GetScoreStatusAsync(gameId);
                    if (!statusResult.Success)
                        continue;

                    var status = statusResult.Data;
                    if (status.status == "verified")
                    {
                        Debug.Log(
                            $"[ScoreSubmitter] Verified: rank={status.rank}, pb={status.isPersonalBest}"
                        );
                        return; // Success — no toast needed
                    }

                    if (status.status == "rejected")
                    {
                        Debug.LogWarning(
                            $"[ScoreSubmitter] Verification rejected: {status.reason}"
                        );
                        ShowError(
                            !string.IsNullOrEmpty(status.reason)
                                ? $"Verification failed: {status.reason}"
                                : "Score could not be verified."
                        );
                        return;
                    }

                    // Still pending, keep polling
                }

                // Exhausted poll attempts — still pending, not an error
                Debug.Log("[ScoreSubmitter] Verification still pending after polling");
                return;
            }

            // Synchronous response (idempotency hit returns 200 directly)
            if (!result.Data.verified)
            {
                string message = DescribeVerificationFailure(result.Data);
                Debug.LogWarning($"[ScoreSubmitter] Verification failed: {message}");
                ShowError(message);
                return;
            }

            // Success — no toast needed
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
            ShowError("Could not submit score.");
        }
    }

    private static void ShowError(string message)
    {
        var toast = GlobalToast.Instance;
        if (toast != null)
            toast.ShowError(message);
    }

    /// <summary>Returns a user-friendly error message based on the failure type.</summary>
    private static string DescribeError(long statusCode, string serverError)
    {
        if (statusCode == 0)
            return "No internet connection.";
        if (statusCode == 401)
            return "Session expired. Please log in again.";
        if (statusCode == 413)
            return "Replay file is too large to upload.";
        if (statusCode == 429)
            return "Too many submissions. Try again later.";
        if (statusCode >= 500)
            return "Server error. Try again later.";

        // 400-level with a server message
        if (!string.IsNullOrEmpty(serverError) && serverError != "Unknown error")
            return serverError;

        return "Could not submit score.";
    }

    /// <summary>Describes a verification failure using the server's reason field.</summary>
    private static string DescribeVerificationFailure(SubmitResultResponse response)
    {
        if (response == null)
            return "Could not submit score.";
        if (!string.IsNullOrEmpty(response.reason))
            return $"Verification failed: {response.reason}";
        return "Score could not be verified.";
    }
}
