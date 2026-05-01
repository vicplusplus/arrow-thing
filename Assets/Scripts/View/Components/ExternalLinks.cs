using System;
using UnityEngine;

/// <summary>
/// Central routing point for all outbound URL navigation.
/// URLs within the game's base route open directly; all others are routed through
/// a scene-provided confirmation handler (typically a modal).
///
/// Usage:
///   ExternalLinks.Open("https://github.com/...");
///
/// Scene controllers subscribe to ExternalLinks.LinkRequested to show the modal:
///   void OnEnable()  => ExternalLinks.LinkRequested += HandleLinkRequest;
///   void OnDisable() => ExternalLinks.LinkRequested -= HandleLinkRequest;
///   void HandleLinkRequest(string url, Action confirm) { /* show modal, call confirm on OK */ }
/// </summary>
public static class ExternalLinks
{
    private const string BaseRoute = "https://arrow-thing.com";

    public const string GitHub = "https://github.com/vicplusplus/arrow-thing";
    public const string Discord = "https://discord.gg/FBwTyaWzpE";

    /// <summary>
    /// Fired when a URL outside the base route is requested.
    /// The handler should show a confirmation UI and call <c>confirm</c> if the user accepts.
    /// </summary>
    public static event Action<string, Action> LinkRequested;

    /// <summary>
    /// Opens <paramref name="url"/>. If it is within the base route it opens immediately;
    /// otherwise it is routed through <see cref="LinkRequested"/> for confirmation.
    /// </summary>
    public static void Open(string url)
    {
        if (url.StartsWith(BaseRoute))
            Application.OpenURL(url);
        else
            LinkRequested?.Invoke(url, () => Application.OpenURL(url));
    }
}
