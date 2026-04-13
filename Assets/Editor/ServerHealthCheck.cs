using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Editor utility to test server connectivity and create test resources.
/// Menu: Tools > Arrow Thing
/// </summary>
public static class ServerHealthCheck
{
    [MenuItem("Tools/Arrow Thing/Check Server Health")]
    public static void CheckHealth()
    {
        var request = UnityWebRequest.Get("http://localhost:5000/health");
        request.timeout = 5;
        var op = request.SendWebRequest();
        EditorApplication.CallbackFunction callback = null;
        callback = () =>
        {
            if (!op.isDone)
                return;
            EditorApplication.update -= callback;

            if (request.result == UnityWebRequest.Result.Success)
                Debug.Log("[ServerHealthCheck] Server is up! (200 OK)");
            else
                Debug.LogWarning($"[ServerHealthCheck] Server unreachable: {request.error}");

            request.Dispose();
        };
        EditorApplication.update += callback;
    }

    [MenuItem("Tools/Arrow Thing/Create Test Lobby")]
    public static async void CreateTestLobby()
    {
        var api = new ApiClient();
        if (!api.IsLoggedIn)
        {
            Debug.LogWarning(
                "[ServerHealthCheck] Not logged in. Log in via the in-game Settings → Account form first."
            );
            return;
        }

        var name = "Test Lobby " + System.DateTime.UtcNow.ToString("HH:mm:ss");
        var result = await api.CreateLobbyAsync(name);
        if (result.Success)
        {
            Debug.Log(
                $"[ServerHealthCheck] Created lobby '{result.Data.name}' "
                    + $"code={result.Data.code} id={result.Data.id} "
                    + $"shareUrl={result.Data.shareUrl}"
            );
        }
        else
        {
            Debug.LogWarning(
                $"[ServerHealthCheck] CreateLobby failed: {result.StatusCode} {result.Error}"
            );
        }
    }
}
