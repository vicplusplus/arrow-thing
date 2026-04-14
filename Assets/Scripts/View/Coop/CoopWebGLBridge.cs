#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine;

/// <summary>
/// MonoBehaviour bridge for the WebGL CoopClient. The .jslib WebSocket
/// callbacks call <c>SendMessage("CoopBridge", "JsOnOpen", ...)</c> etc.,
/// which Unity dispatches to instance methods on a MonoBehaviour attached
/// to a GameObject named "CoopBridge". This class parses the messages and
/// routes them to <see cref="CoopClient"/>'s static handlers.
/// </summary>
public class CoopWebGLBridge : MonoBehaviour
{
    private static CoopWebGLBridge _instance;

    public static void EnsureExists()
    {
        if (_instance != null)
            return;
        var go = new GameObject("CoopBridge");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<CoopWebGLBridge>();
    }

    public void JsOnOpen(string handleStr)
    {
        if (int.TryParse(handleStr, out var handle))
            CoopClient.OnJsOpen(handle);
    }

    public void JsOnMessage(string payload)
    {
        // Format: "{handle}|{json}"
        var sepIdx = payload.IndexOf('|');
        if (sepIdx < 0)
            return;
        if (!int.TryParse(payload.Substring(0, sepIdx), out var handle))
            return;
        var json = payload.Substring(sepIdx + 1);
        CoopClient.OnJsMessage(handle, json);
    }

    public void JsOnBinary(string payload)
    {
        // Format: "{handle}|{base64}"
        var sepIdx = payload.IndexOf('|');
        if (sepIdx < 0)
            return;
        if (!int.TryParse(payload.Substring(0, sepIdx), out var handle))
            return;
        var base64 = payload.Substring(sepIdx + 1);
        CoopClient.OnJsBinary(handle, base64);
    }

    public void JsOnClose(string payload)
    {
        var sepIdx = payload.IndexOf('|');
        if (sepIdx < 0)
            return;
        if (!int.TryParse(payload.Substring(0, sepIdx), out var handle))
            return;
        var reason = payload.Substring(sepIdx + 1);
        CoopClient.OnJsClose(handle, reason);
    }
}
#endif
