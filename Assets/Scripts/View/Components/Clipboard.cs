using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>
/// Cross-platform clipboard write. On desktop/editor uses
/// <c>GUIUtility.systemCopyBuffer</c>; on WebGL uses a .jslib wrapper around
/// the browser Clipboard API.
/// </summary>
public static class Clipboard
{
#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void Clipboard_WriteText(string text);
#endif

    public static void WriteText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

#if UNITY_WEBGL && !UNITY_EDITOR
        Clipboard_WriteText(text);
#else
        GUIUtility.systemCopyBuffer = text;
#endif
    }
}
