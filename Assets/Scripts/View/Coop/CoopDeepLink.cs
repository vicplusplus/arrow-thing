using System;
using System.Linq;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>
/// Reads a lobby code from the environment at boot:
///   WebGL  — <c>?lobby=XXXXXX</c> from <c>window.location.search</c> via a .jslib helper.
///   Desktop/Editor — <c>--lobby=XXXXXX</c> from the command-line args.
///
/// Sets <see cref="GameSettings.PendingLobbyCode"/> if a valid code is found.
/// Call <see cref="TryResolve"/> once early in the app lifecycle (e.g. MainMenuController.OnEnable).
/// </summary>
public static class CoopDeepLink
{
    private static bool _resolved;

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern IntPtr CoopDeepLink_Get();
#endif

    /// <summary>
    /// Reads the deep-link code from the environment and populates
    /// <see cref="GameSettings.PendingLobbyCode"/> if valid. Safe to call
    /// multiple times — only the first call has an effect.
    /// </summary>
    public static void TryResolve()
    {
        if (_resolved)
            return;
        _resolved = true;

        string code = ReadCodeFromEnvironment();
        if (IsValidCode(code))
        {
            GameSettings.PendingLobbyCode = code.ToUpperInvariant();
            Debug.Log($"[CoopDeepLink] Resolved lobby code: {GameSettings.PendingLobbyCode}");
        }
    }

    private static string ReadCodeFromEnvironment()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try
        {
            var ptr = CoopDeepLink_Get();
            if (ptr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringAnsi(ptr);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CoopDeepLink] WebGL read failed: {e.Message}");
            return null;
        }
#else
        // Desktop / Editor: scan command-line args for --lobby=XXXXXX
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--lobby=", StringComparison.OrdinalIgnoreCase))
                return arg.Substring("--lobby=".Length);
        }
        return null;
#endif
    }

    private static bool IsValidCode(string code)
    {
        if (string.IsNullOrEmpty(code) || code.Length != 6)
            return false;
        // Allow letters and digits — server rejects invalid codes.
        return code.All(c =>
            (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
        );
    }
}
