using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Lobby-complete results overlay. Shows the final roster sorted by clear
/// count, with medals for top 3 and the own-player row highlighted.
///
/// Buttons:
///  - Play Again → Coop Hub (same lobby can be rejoined for results view, or a new one hosted).
///  - Menu → main menu.
///  - View Replay → fetches the v6 replay and enters the replay scene.
/// </summary>
public sealed class CoopResultsScreen
{
    private readonly IReadOnlyDictionary<Guid, CoopPlayer> _roster;
    private readonly Guid _selfId;
    private readonly string _lobbyCode;
    private readonly VisualElement _root;
    private VisualElement _overlay;
    private Action _onViewReplay;

    public CoopResultsScreen(
        VisualElement hudRoot,
        IReadOnlyDictionary<Guid, CoopPlayer> roster,
        Guid selfId,
        string lobbyCode,
        Action onViewReplay = null
    )
    {
        _root = hudRoot;
        _roster = roster;
        _selfId = selfId;
        _lobbyCode = lobbyCode;
        _onViewReplay = onViewReplay;
        Build();
    }

    public void Show() => _overlay.RemoveFromClassList("modal--hidden");

    public void Dispose() => _overlay?.RemoveFromHierarchy();

    // ── UI construction ────────────────────────────────────────────────────

    private void Build()
    {
        _overlay = new VisualElement();
        _overlay.AddToClassList("modal-overlay");
        _overlay.AddToClassList("coop-results");
        _overlay.AddToClassList("modal--hidden");

        var box = new VisualElement();
        box.AddToClassList("modal-box");
        box.AddToClassList("coop-results__box");
        _overlay.Add(box);

        var title = new Label("Board cleared!");
        title.AddToClassList("coop-results__title");
        box.Add(title);

        var scroll = new ScrollView(ScrollViewMode.Vertical);
        scroll.AddToClassList("coop-results__scroll");
        box.Add(scroll);

        var sorted = SortForResults();
        for (int i = 0; i < sorted.Count; i++)
            scroll.contentContainer.Add(BuildRow(i + 1, sorted[i]));

        var btnRow = new VisualElement();
        btnRow.AddToClassList("coop-results__buttons");
        box.Add(btnRow);

        var replayBtn = new Button(() => _onViewReplay?.Invoke()) { text = "View Replay" };
        replayBtn.AddToClassList("coop-results__btn");
        if (_onViewReplay == null)
            replayBtn.SetEnabled(false);
        btnRow.Add(replayBtn);

        var playAgainBtn = new Button(() => SceneNav.Replace("CoopHub")) { text = "Play Again" };
        playAgainBtn.AddToClassList("coop-results__btn");
        playAgainBtn.AddToClassList("coop-results__btn--primary");
        btnRow.Add(playAgainBtn);

        var menuBtn = new Button(() => SceneNav.Replace("MainMenu")) { text = "Menu" };
        menuBtn.AddToClassList("coop-results__btn");
        btnRow.Add(menuBtn);

        _root.Add(_overlay);
    }

    private VisualElement BuildRow(int rank, CoopPlayer player)
    {
        var row = new VisualElement();
        row.AddToClassList("coop-results-row");
        if (player.Id == _selfId)
            row.AddToClassList("coop-results-row--self");
        if (rank == 1)
            row.AddToClassList("coop-results-row--gold");
        else if (rank == 2)
            row.AddToClassList("coop-results-row--silver");
        else if (rank == 3)
            row.AddToClassList("coop-results-row--bronze");

        var rankLabel = new Label($"#{rank}");
        rankLabel.AddToClassList("coop-results-row__rank");
        row.Add(rankLabel);

        var dot = new VisualElement();
        dot.AddToClassList("coop-results-row__dot");
        dot.style.backgroundColor = new StyleColor(player.Color);
        row.Add(dot);

        var name = new Label(
            string.IsNullOrEmpty(player.DisplayName) ? "(anon)" : player.DisplayName
        );
        name.AddToClassList("coop-results-row__name");
        row.Add(name);

        var count = new Label($"{player.ClearCount} clears");
        count.AddToClassList("coop-results-row__count");
        row.Add(count);

        var time = new Label(FormatTime(player.AccumulatedMillis));
        time.AddToClassList("coop-results-row__time");
        row.Add(time);

        return row;
    }

    private List<CoopPlayer> SortForResults()
    {
        return _roster
            .Values.OrderByDescending(p => p.ClearCount)
            .ThenBy(p => p.AccumulatedMillis == 0 ? long.MaxValue : p.AccumulatedMillis)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string FormatTime(long millis)
    {
        if (millis <= 0)
            return "—";
        var total = TimeSpan.FromMilliseconds(millis);
        if (total.TotalHours >= 1)
            return $"{(int)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2}";
        return $"{total.Minutes:D2}:{total.Seconds:D2}";
    }
}
