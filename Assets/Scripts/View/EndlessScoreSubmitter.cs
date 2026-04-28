using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Endless-mode counterpart to <see cref="ScoreSubmitter"/>. Fire-and-forget
/// submission of a topped-out endless replay to the server's
/// <c>/api/endless-scores</c> endpoint, with the same poll-for-async-
/// verification + retryable-error toast UX classic uses. Verification status
/// shares the classic <c>/api/scores/{gameId}/status</c> endpoint (Redis
/// keyspace is shared between modes).
/// </summary>
public static class EndlessScoreSubmitter
{
    private const int PollAttempts = 3;
    private const float PollIntervalSeconds = 2f;

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
            var result = await api.SubmitEndlessScoreAsync(replayJson);
            if (!result.Success)
            {
                string message = DescribeError(result.StatusCode, result.Error);
                Debug.LogWarning(
                    $"[EndlessScoreSubmitter] Submission failed ({result.StatusCode}): {message}"
                );
                if (IsRetryable(result.StatusCode))
                    ShowRetryableError(message, api, replayJson);
                else
                    ShowError(message);
                return;
            }

            if (result.IsAccepted)
            {
                var gameId = result.AcceptedGameId;
                Debug.Log(
                    $"[EndlessScoreSubmitter] Score accepted for verification (gameId={gameId}), polling..."
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
                            $"[EndlessScoreSubmitter] Verified: rank={status.rank}, pb={status.isPersonalBest}"
                        );
                        DismissToast();
                        return;
                    }
                    if (status.status == "rejected")
                    {
                        Debug.LogWarning(
                            $"[EndlessScoreSubmitter] Verification rejected: {status.reason}"
                        );
                        ShowError(
                            !string.IsNullOrEmpty(status.reason)
                                ? $"Verification failed: {status.reason}"
                                : "Score could not be verified."
                        );
                        return;
                    }
                }
                Debug.Log("[EndlessScoreSubmitter] Verification still pending after polling");
                ShowInfo("Score submitted — verification in progress.");
                return;
            }

            // Synchronous response (idempotent retry hits 200).
            if (!result.Data.verified)
            {
                ShowError(
                    !string.IsNullOrEmpty(result.Data.reason)
                        ? $"Verification failed: {result.Data.reason}"
                        : "Score could not be verified."
                );
                return;
            }
            DismissToast();
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex);
            ShowRetryableError("Could not submit score.", api, replayJson);
        }
    }

    private static bool IsRetryable(long statusCode) =>
        statusCode == 0 || statusCode == 408 || statusCode == 429 || statusCode >= 500;

    private static string DescribeError(long statusCode, string serverError) =>
        statusCode == 0 ? "No internet connection."
        : statusCode == 401 ? "Session expired. Please log in again."
        : statusCode == 413 ? "Replay file is too large to upload."
        : statusCode == 429 ? "Too many submissions. Try again later."
        : statusCode >= 500 ? "Server error. Try again later."
        : !string.IsNullOrEmpty(serverError) && serverError != "Unknown error" ? serverError
        : "Could not submit score.";

    private static void DismissToast() => GlobalToast.Instance?.Dismiss();

    private static void ShowInfo(string message) => GlobalToast.Instance?.ShowInfo(message);

    private static void ShowError(string message) => GlobalToast.Instance?.ShowError(message);

    private static void ShowRetryableError(string message, ApiClient api, string replayJson) =>
        GlobalToast.Instance?.ShowRetryableError(
            message,
            () => SubmitInBackground(api, replayJson)
        );
}
