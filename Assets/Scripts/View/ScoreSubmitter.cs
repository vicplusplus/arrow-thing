using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>
/// Result of a score submission attempt. Carries either a successful server response
/// or a user-facing error message describing what went wrong.
/// </summary>
public class SubmitResult
{
    public SubmitResultResponse Response { get; private set; }
    public string Error { get; private set; }
    public bool IsPending { get; private set; }

    public bool IsSuccess => Response != null && Response.verified;

    public static SubmitResult Success(SubmitResultResponse response) =>
        new SubmitResult { Response = response };

    public static SubmitResult Fail(string error) => new SubmitResult { Error = error };

    public static SubmitResult Pending() => new SubmitResult { IsPending = true };

    /// <summary>Returns a user-friendly error message based on the failure type.</summary>
    public static string DescribeError(long statusCode, string serverError)
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
    public static string DescribeVerificationFailure(SubmitResultResponse response)
    {
        if (response == null)
            return "Could not submit score.";
        if (!string.IsNullOrEmpty(response.reason))
            return $"Verification failed: {response.reason}";
        return "Score could not be verified.";
    }
}

/// <summary>
/// Static helper for submitting scores to the global leaderboard.
/// Creates its own ApiClient (reads saved JWT from PlayerPrefs).
/// </summary>
public static class ScoreSubmitter
{
    private const int PollAttempts = 3;
    private const float PollIntervalSeconds = 2f;

    /// <summary>
    /// Attempts to submit a completed replay to the server.
    /// Returns a <see cref="SubmitResult"/> with either success data or a descriptive error.
    /// </summary>
    public static async Task<SubmitResult> TrySubmitAsync(ReplayData replay)
    {
        var api = new ApiClient();
        if (!api.IsLoggedIn)
            return SubmitResult.Fail("Not logged in.");

        var replayJson = JsonConvert.SerializeObject(replay);
        var result = await api.SubmitScoreAsync(replayJson);

        if (!result.Success)
        {
            string message = SubmitResult.DescribeError(result.StatusCode, result.Error);
            Debug.LogWarning(
                $"[ScoreSubmitter] Submission failed ({result.StatusCode}): {message}"
            );
            return SubmitResult.Fail(message);
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
                    return SubmitResult.Success(
                        new SubmitResultResponse
                        {
                            verified = true,
                            rank = status.rank,
                            isPersonalBest = status.isPersonalBest,
                        }
                    );
                }

                if (status.status == "rejected")
                {
                    Debug.LogWarning($"[ScoreSubmitter] Verification rejected: {status.reason}");
                    return SubmitResult.Fail(
                        !string.IsNullOrEmpty(status.reason)
                            ? $"Verification failed: {status.reason}"
                            : "Score could not be verified."
                    );
                }

                // Still pending, keep polling
            }

            // Exhausted poll attempts — still pending
            Debug.Log("[ScoreSubmitter] Verification still pending after polling");
            return SubmitResult.Pending();
        }

        // Synchronous response (idempotency hit returns 200 directly)
        if (!result.Data.verified)
        {
            string message = SubmitResult.DescribeVerificationFailure(result.Data);
            Debug.LogWarning($"[ScoreSubmitter] Verification failed: {message}");
            return SubmitResult.Fail(message);
        }

        return SubmitResult.Success(result.Data);
    }
}
