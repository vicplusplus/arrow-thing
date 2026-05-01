using System;
using UnityEngine.UIElements;

/// <summary>
/// Per-row VisualElement builder for the leaderboard's global view.
/// Parallel to <see cref="LeaderboardEntryRow"/> (which builds local
/// rows): different shape — global rows show only a play button (no
/// favorite or context-menu trigger), and the highlight tint marks the
/// viewer's own entry rather than a favorite.
///
/// <para>Stateless across calls. Construction takes a <see cref="Callbacks"/>
/// bundle so the row can hand the play action back to the controller
/// without depending on its instance state. <see cref="Build"/> returns
/// the assembled row VisualElement; the caller adds it to the list.</para>
/// </summary>
public sealed class LeaderboardGlobalEntryRow
{
    /// <summary>Per-instance hooks the row calls when the user activates the play button.</summary>
    public sealed class Callbacks
    {
        /// <summary>Launch the global replay for the given gameId.</summary>
        public Action<string> OnPlay;

        /// <summary>
        /// Register hover-scroll on the row's name wrapper + label. The
        /// controller owns the scroll animation state; this just hands
        /// the elements over for hookup. Mirrors
        /// <c>LeaderboardEntryRow.Callbacks.RegisterNameScroll</c>.
        /// </summary>
        public Action<VisualElement, Label> RegisterNameScroll;
    }

    private readonly Callbacks _cb;

    public LeaderboardGlobalEntryRow(Callbacks callbacks)
    {
        _cb = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    /// <summary>
    /// Build a global-entry row from <paramref name="entry"/>.
    /// <paramref name="showSize"/> on the cross-config All tab adds the
    /// size column and uses compact time format.
    /// <paramref name="highlight"/> tints the row when it's the viewer's
    /// own entry (driven by the player-panel `me` data).
    /// </summary>
    public VisualElement Build(GlobalLeaderboardEntry entry, bool showSize, bool highlight)
    {
        var row = new VisualElement();
        row.AddToClassList("lb-entry");
        if (highlight)
            row.AddToClassList("lb-entry--highlight");

        // Medal highlights — top-3 across the entire fetched page.
        if (entry.rank == 1)
            row.AddToClassList("lb-entry--gold");
        else if (entry.rank == 2)
            row.AddToClassList("lb-entry--silver");
        else if (entry.rank == 3)
            row.AddToClassList("lb-entry--bronze");

        // Rank.
        var rankLabel = new Label($"#{entry.rank}");
        rankLabel.AddToClassList("lb-rank");
        if (entry.rank == 1)
            rankLabel.AddToClassList("lb-rank--gold");
        else if (entry.rank == 2)
            rankLabel.AddToClassList("lb-rank--silver");
        else if (entry.rank == 3)
            rankLabel.AddToClassList("lb-rank--bronze");
        row.Add(rankLabel);

        // Size (All tab only).
        if (showSize)
        {
            var sizeLabel = new Label($"{entry.boardWidth}x{entry.boardHeight}");
            sizeLabel.AddToClassList("lb-size");
            row.Add(sizeLabel);
        }

        // Time (compact format on All tab).
        string timeText = showSize
            ? LeaderboardFormatters.FormatCompactTime(entry.time)
            : LeaderboardFormatters.FormatTime(entry.time);
        var timeLabel = new Label(timeText);
        timeLabel.AddToClassList("lb-time");
        if (showSize)
            timeLabel.AddToClassList("lb-time--compact");
        row.Add(timeLabel);

        // Display name — wrapped for horizontal auto-scroll on overflow.
        var nameWrapper = new VisualElement();
        nameWrapper.AddToClassList("lb-name-wrapper");
        var nameLabel = new Label(entry.displayName ?? "");
        nameLabel.AddToClassList("lb-name");
        nameWrapper.Add(nameLabel);
        row.Add(nameWrapper);
        _cb.RegisterNameScroll(nameWrapper, nameLabel);

        // Play button (replay).
        string capturedGameId = entry.gameId;
        var playBtn = new Button(() => _cb.OnPlay(capturedGameId));
        playBtn.AddToClassList("lb-row-btn");
        var playIcon = new VisualElement();
        playIcon.AddToClassList("lb-row-btn__icon");
        playIcon.AddToClassList("lb-play-icon");
        playBtn.Add(playIcon);
        row.Add(playBtn);

        return row;
    }
}
