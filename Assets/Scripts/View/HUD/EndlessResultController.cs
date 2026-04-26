using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// End-of-run result screen for endless mode. Mirrors
/// <see cref="VictoryController"/>'s overlay pattern but tailored to topout:
/// shows clears, longest combo, run duration, plus play-again / menu buttons.
///
/// Reuses the existing <c>victory-overlay</c> container in
/// <c>Assets/UI/Game/VictoryPopup.uxml</c> (and its CSS) by rewriting the
/// container's children — saves shipping a parallel UXML / UIDocument and
/// editor wiring.
///
/// Phase 4b scope: no leaderboard submission, no replay save. Endless leader-
/// boards / replays are deferred to a later milestone (see
/// <c>docs/GameControllerDecompositionPlan.md</c>, stage C1 — endless save
/// support naturally lives there).
/// </summary>
public sealed class EndlessResultController : MonoBehaviour
{
    private UIDocument _victoryDocument;
    private VisualElement _overlay;

    /// <summary>
    /// Builds the result overlay using the supplied stats. Idempotent — calling
    /// twice rebuilds the contents.
    /// </summary>
    public void Init(
        UIDocument victoryDocument,
        int clears,
        int longestCombo,
        float runDurationSeconds
    )
    {
        if (victoryDocument == null)
            throw new ArgumentNullException(nameof(victoryDocument));
        _victoryDocument = victoryDocument;

        var root = victoryDocument.rootVisualElement;
        _overlay = root?.Q<VisualElement>("victory-overlay");
        if (_overlay == null)
        {
            Debug.LogWarning(
                "[EndlessResultController] victory-overlay element not found; cannot show result."
            );
            return;
        }

        // Replace whatever VictoryController would put here (it isn't wired in
        // endless mode) with the endless-specific layout.
        _overlay.Clear();
        _overlay.RemoveFromClassList("victory--hidden");

        var box = new VisualElement();
        box.AddToClassList("victory-box");
        _overlay.Add(box);

        var headline = new Label("Topped out") { name = "endless-result-headline" };
        headline.AddToClassList("victory-message");
        box.Add(headline);

        var clearsLabel = new Label($"{clears} cleared") { name = "endless-result-clears" };
        clearsLabel.AddToClassList("victory-time");
        box.Add(clearsLabel);

        var comboLabel = new Label($"Longest combo: {longestCombo}")
        {
            name = "endless-result-combo",
        };
        comboLabel.AddToClassList("victory-time");
        box.Add(comboLabel);

        var durationLabel = new Label(FormatTime(runDurationSeconds))
        {
            name = "endless-result-duration",
        };
        durationLabel.AddToClassList("victory-time");
        box.Add(durationLabel);

        var playAgainBtn = new Button(OnPlayAgain) { text = "Play Again" };
        playAgainBtn.AddToClassList("victory-btn");
        playAgainBtn.AddToClassList("victory-btn--primary");
        box.Add(playAgainBtn);

        var menuBtn = new Button(OnMenu) { text = "Menu" };
        menuBtn.AddToClassList("victory-btn");
        box.Add(menuBtn);
    }

    private void OnPlayAgain()
    {
        // Stays in endless mode — Mode flag is preserved through the scene reload.
        SceneNav.Replace("Game");
    }

    private void OnMenu()
    {
        SceneNav.Pop();
    }

    /// <summary>
    /// Mirrors <see cref="VictoryController.FormatTime"/>: <c>h:mm:ss.fff</c> /
    /// <c>m:ss.fff</c> / <c>s.fff</c> depending on duration.
    /// </summary>
    private static string FormatTime(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        int totalMillis = (int)(seconds * 1000);
        int hours = totalMillis / 3600000;
        int mins = (totalMillis % 3600000) / 60000;
        int secs = (totalMillis % 60000) / 1000;
        int millis = totalMillis % 1000;

        if (hours > 0)
            return $"{hours}:{mins:D2}:{secs:D2}.{millis:D3}";
        if (mins > 0)
            return $"{mins}:{secs:D2}.{millis:D3}";
        return $"{secs}.{millis:D3}";
    }
}
