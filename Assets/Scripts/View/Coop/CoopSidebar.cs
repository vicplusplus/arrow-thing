using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Live roster sidebar for co-op mode. Mounts as an absolute-positioned
/// overlay into the HUD root; re-renders on <see cref="CoopSession.RosterUpdated"/>.
///
/// Layout:
/// - Top 10 players by clear count (own row pinned at top even if outside top 10).
/// - "Show all (N)" button opens a modal with the full roster when total > 10.
/// - On narrow viewports (&lt; 500 px), the sidebar collapses into a
///   player-count pill in the top-right; tapping the pill opens the full
///   modal in place of the sidebar.
/// </summary>
public sealed class CoopSidebar
{
    private const int TopNVisible = 10;
    private const float NarrowThresholdPx = 500f;

    private readonly CoopSession _session;
    private readonly VisualElement _root;

    private VisualElement _panel;
    private VisualElement _header;
    private Label _headerLabel;
    private ScrollView _rowsScroll;
    private VisualElement _rows;
    private Button _showAllBtn;

    private VisualElement _pill;
    private Label _pillLabel;

    private VisualElement _modal;
    private ScrollView _modalRows;

    private bool _narrow;

    public CoopSidebar(CoopSession session, VisualElement hudRoot)
    {
        _session = session;
        _root = hudRoot;
        BuildUi();
        _session.RosterUpdated += Render;
        hudRoot.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        Render();
    }

    public void Dispose()
    {
        _session.RosterUpdated -= Render;
        _panel?.RemoveFromHierarchy();
        _pill?.RemoveFromHierarchy();
        _modal?.RemoveFromHierarchy();
    }

    // ── UI construction ────────────────────────────────────────────────────

    private void BuildUi()
    {
        // Sidebar panel (wide viewport default)
        _panel = new VisualElement();
        _panel.AddToClassList("coop-sidebar");
        _root.Add(_panel);

        _header = new VisualElement();
        _header.AddToClassList("coop-sidebar__header");
        _panel.Add(_header);

        _headerLabel = new Label("Players");
        _headerLabel.AddToClassList("coop-sidebar__title");
        _header.Add(_headerLabel);

        _rowsScroll = new ScrollView(ScrollViewMode.Vertical);
        _rowsScroll.AddToClassList("coop-sidebar__scroll");
        _panel.Add(_rowsScroll);
        _rows = _rowsScroll.contentContainer;

        _showAllBtn = new Button(OpenAllPlayersModal) { text = "Show all" };
        _showAllBtn.AddToClassList("coop-sidebar__showall");
        _showAllBtn.style.display = DisplayStyle.None;
        _panel.Add(_showAllBtn);

        // Narrow-mode pill
        _pill = new Button(OpenAllPlayersModal) { text = "0" };
        _pill.AddToClassList("coop-sidebar-pill");
        _pill.style.display = DisplayStyle.None;
        _pillLabel = new Label("players");
        _pillLabel.AddToClassList("coop-sidebar-pill__label");
        _pill.Add(_pillLabel);
        _root.Add(_pill);

        // All-players modal (hidden by default)
        _modal = new VisualElement();
        _modal.AddToClassList("modal-overlay");
        _modal.AddToClassList("coop-roster-modal");
        _modal.AddToClassList("modal--hidden");
        var modalBox = new VisualElement();
        modalBox.AddToClassList("modal-box");
        modalBox.AddToClassList("coop-roster-modal__box");
        _modal.Add(modalBox);
        var modalHeader = new VisualElement();
        modalHeader.AddToClassList("coop-roster-modal__header");
        modalBox.Add(modalHeader);
        var modalTitle = new Label("All players");
        modalTitle.AddToClassList("coop-roster-modal__title");
        modalHeader.Add(modalTitle);
        var modalClose = new Button(CloseAllPlayersModal) { text = "×" };
        modalClose.AddToClassList("coop-roster-modal__close");
        modalHeader.Add(modalClose);
        _modalRows = new ScrollView(ScrollViewMode.Vertical);
        _modalRows.AddToClassList("coop-roster-modal__scroll");
        modalBox.Add(_modalRows);
        _root.Add(_modal);

        // Click-outside-to-dismiss on the overlay background.
        _modal.RegisterCallback<ClickEvent>(e =>
        {
            if (e.target == _modal)
                CloseAllPlayersModal();
        });
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    private void Render()
    {
        var sorted = SortRoster(_session.Roster);
        int total = sorted.Count;
        _headerLabel.text = $"Players ({total})";
        _pill.text = total.ToString();

        _rows.Clear();
        int shown = 0;
        Guid selfId = _session.YourUserId;
        // Pin own row first if present, even if outside the top-N range.
        if (_session.Roster.TryGetValue(selfId, out var selfPlayer))
        {
            _rows.Add(BuildRow(selfPlayer, isSelf: true));
            shown++;
        }
        foreach (var p in sorted)
        {
            if (shown >= TopNVisible)
                break;
            if (p.Id == selfId)
                continue;
            _rows.Add(BuildRow(p, isSelf: false));
            shown++;
        }

        _showAllBtn.style.display = total > TopNVisible ? DisplayStyle.Flex : DisplayStyle.None;
        _showAllBtn.text = $"Show all ({total})";

        // If the modal is open, refresh its content too.
        if (IsModalOpen())
            RenderModalRows(sorted);
    }

    private void RenderModalRows(List<CoopPlayer> sorted)
    {
        _modalRows.contentContainer.Clear();
        Guid selfId = _session.YourUserId;
        if (_session.Roster.TryGetValue(selfId, out var self))
            _modalRows.contentContainer.Add(BuildRow(self, isSelf: true));
        foreach (var p in sorted)
        {
            if (p.Id == selfId)
                continue;
            _modalRows.contentContainer.Add(BuildRow(p, isSelf: false));
        }
    }

    private VisualElement BuildRow(CoopPlayer player, bool isSelf)
    {
        var row = new VisualElement();
        row.AddToClassList("coop-roster-row");
        if (isSelf)
            row.AddToClassList("coop-roster-row--self");
        if (!player.Online)
            row.AddToClassList("coop-roster-row--offline");

        var dot = new VisualElement();
        dot.AddToClassList("coop-roster-row__dot");
        dot.style.backgroundColor = new StyleColor(player.Color);
        row.Add(dot);

        var name = new Label(
            string.IsNullOrEmpty(player.DisplayName) ? "(anon)" : player.DisplayName
        );
        name.AddToClassList("coop-roster-row__name");
        row.Add(name);

        var time = new Label(FormatTime(player.AccumulatedMillis));
        time.AddToClassList("coop-roster-row__time");
        row.Add(time);

        var count = new Label($"#{player.ClearCount}");
        count.AddToClassList("coop-roster-row__count");
        row.Add(count);

        return row;
    }

    private static string FormatTime(long millis)
    {
        if (millis <= 0)
            return "—";
        var total = System.TimeSpan.FromMilliseconds(millis);
        if (total.TotalHours >= 1)
            return $"{(int)total.TotalHours}:{total.Minutes:D2}:{total.Seconds:D2}";
        return $"{total.Minutes:D2}:{total.Seconds:D2}";
    }

    private static List<CoopPlayer> SortRoster(IReadOnlyDictionary<Guid, CoopPlayer> roster)
    {
        // Primary: ClearCount DESC. Secondary: AccumulatedMillis ASC (faster players rise).
        // Offline players sink (lower priority within ties).
        return roster
            .Values.OrderByDescending(p => p.Online)
            .ThenByDescending(p => p.ClearCount)
            .ThenBy(p => p.AccumulatedMillis == 0 ? long.MaxValue : p.AccumulatedMillis)
            .ThenBy(p => p.DisplayName, System.StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Modal + layout ─────────────────────────────────────────────────────

    private void OpenAllPlayersModal()
    {
        _modal.RemoveFromClassList("modal--hidden");
        var sorted = SortRoster(_session.Roster);
        RenderModalRows(sorted);
    }

    private void CloseAllPlayersModal()
    {
        _modal.AddToClassList("modal--hidden");
    }

    private bool IsModalOpen() => !_modal.ClassListContains("modal--hidden");

    private void OnGeometryChanged(GeometryChangedEvent evt)
    {
        bool narrow = evt.newRect.width < NarrowThresholdPx;
        if (narrow == _narrow)
            return;
        _narrow = narrow;
        _panel.style.display = narrow ? DisplayStyle.None : DisplayStyle.Flex;
        _pill.style.display = narrow ? DisplayStyle.Flex : DisplayStyle.None;
        if (narrow && IsModalOpen())
        {
            // Keep the modal open; it already serves as the narrow-mode list.
        }
    }
}
