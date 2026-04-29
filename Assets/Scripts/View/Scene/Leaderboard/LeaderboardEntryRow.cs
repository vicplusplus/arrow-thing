using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

/// <summary>
/// Builds a single classic-leaderboard row VisualElement from a
/// <see cref="LeaderboardEntry"/>. Pulled out of
/// <c>LeaderboardScreenController</c> so the row layout and the
/// controller's render orchestration / state aren't braided together.
///
/// <para>Construction-time callbacks carry the controller-side hooks the
/// row needs (favorite toggle, replay launch, context menu open, name
/// hover-scroll registration, "is the active sort favorites?" lookup).
/// The row itself is otherwise stateless — every render call gets a
/// fresh medal context + rank + entry, and the resulting VisualElement
/// is ready to <c>_list.Add()</c>.</para>
/// </summary>
public sealed class LeaderboardEntryRow
{
    /// <summary>
    /// Controller hooks the row delegates back to. All required —
    /// fields are deliberately not nullable to make wiring mistakes
    /// fail fast at construction.
    /// </summary>
    public sealed class Callbacks
    {
        /// <summary>True when the active sort is Favorites — drives the row's favorite-tint class.</summary>
        public Func<bool> IsFavoritesSort;

        /// <summary>Toggle favorite for the given gameId, given the current favorited state.</summary>
        public Action<string, bool> OnToggleFavorite;

        /// <summary>Launch replay for the given gameId.</summary>
        public Action<string> OnPlay;

        /// <summary>
        /// Open the context menu for the given (gameId, isFavorited) anchored
        /// on the row's VisualElement so the menu can position itself
        /// relative to the row.
        /// </summary>
        public Action<string, bool, VisualElement> OnContextMenu;

        /// <summary>
        /// Register hover-scroll on the row's name wrapper + label. The
        /// controller owns the actual scroll animation state; this just
        /// hands the elements over for hookup.
        /// </summary>
        public Action<VisualElement, Label> RegisterNameScroll;
    }

    /// <summary>
    /// Top-3 highlight set for a given render pass. Empty / null sets
    /// in any slot mean "no medal of that color" — useful for the
    /// Favorites sort, which suppresses medals entirely by passing
    /// nulls.
    /// </summary>
    public readonly struct Medals
    {
        public readonly HashSet<string> Gold;
        public readonly HashSet<string> Silver;
        public readonly HashSet<string> Bronze;

        public Medals(HashSet<string> gold, HashSet<string> silver, HashSet<string> bronze)
        {
            Gold = gold;
            Silver = silver;
            Bronze = bronze;
        }

        public static readonly Medals None = new Medals(null, null, null);
    }

    private readonly Callbacks _cb;

    public LeaderboardEntryRow(Callbacks callbacks)
    {
        _cb = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    /// <summary>
    /// Builds a row VisualElement for <paramref name="entry"/>. <paramref name="rank"/>
    /// is the displayed rank (1-based). <paramref name="showSize"/> on the
    /// All tab adds a size column and uses compact time format. Caller is
    /// responsible for adding the returned element to its list container.
    /// </summary>
    public VisualElement Build(int rank, LeaderboardEntry entry, bool showSize, Medals medals)
    {
        var row = new VisualElement();
        row.AddToClassList("lb-entry");
        row.userData = entry.gameId;

        // Medal highlights (non-Favorites sort).
        string medalRow = null;
        string medalRank = null;
        if (medals.Gold != null && medals.Gold.Contains(entry.gameId))
        {
            medalRow = "lb-entry--gold";
            medalRank = "lb-rank--gold";
        }
        else if (medals.Silver != null && medals.Silver.Contains(entry.gameId))
        {
            medalRow = "lb-entry--silver";
            medalRank = "lb-rank--silver";
        }
        else if (medals.Bronze != null && medals.Bronze.Contains(entry.gameId))
        {
            medalRow = "lb-entry--bronze";
            medalRank = "lb-rank--bronze";
        }

        // Favorite tint (Favorites sort only — medals are null).
        if (medalRow == null && _cb.IsFavoritesSort() && entry.isFavorite)
            row.AddToClassList("lb-entry--favorite");
        if (medalRow != null)
            row.AddToClassList(medalRow);

        // Rank.
        var rankLabel = new Label($"#{rank}");
        rankLabel.AddToClassList("lb-rank");
        if (medalRank != null)
            rankLabel.AddToClassList(medalRank);
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
            ? LeaderboardFormatters.FormatCompactTime(entry.solveTime)
            : LeaderboardFormatters.FormatTime(entry.solveTime);
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

        // Date (relative text, tooltip shows exact date+time).
        var dateLabel = new Label(LeaderboardFormatters.FormatRelativeDate(entry.completedAt));
        dateLabel.AddToClassList("lb-date");
        dateLabel.tooltip = LeaderboardFormatters.FormatExactDate(entry.completedAt);
        row.Add(dateLabel);

        // Capture identity for click handlers — `entry` is the pass-time
        // value; closing over `entry.gameId` would still be safe in C#
        // but the explicit local makes the intent unmistakable.
        string capturedGameId = entry.gameId;
        bool capturedFav = entry.isFavorite;

        // Favorite icon (clickable toggle).
        var favBtn = new Button(() => _cb.OnToggleFavorite(capturedGameId, capturedFav));
        favBtn.AddToClassList("lb-row-btn");
        favBtn.AddToClassList("lb-fav-btn");
        var favIcon = new VisualElement();
        favIcon.AddToClassList("lb-row-btn__icon");
        favIcon.AddToClassList(entry.isFavorite ? "lb-fav-icon--on" : "lb-fav-icon--off");
        favBtn.Add(favIcon);
        row.Add(favBtn);

        // Play button.
        var playBtn = new Button(() => _cb.OnPlay(capturedGameId));
        playBtn.AddToClassList("lb-row-btn");
        var playIcon = new VisualElement();
        playIcon.AddToClassList("lb-row-btn__icon");
        playIcon.AddToClassList("lb-play-icon");
        playBtn.Add(playIcon);
        row.Add(playBtn);

        // Context menu trigger.
        var ctxBtn = new Button(() => _cb.OnContextMenu(capturedGameId, capturedFav, row));
        ctxBtn.AddToClassList("lb-row-btn");
        ctxBtn.AddToClassList("lb-ctx-trigger");
        var ctxIcon = new VisualElement();
        ctxIcon.AddToClassList("lb-row-btn__icon");
        ctxIcon.AddToClassList("lb-ctx-trigger-icon");
        ctxBtn.Add(ctxIcon);
        row.Add(ctxBtn);

        return row;
    }
}
