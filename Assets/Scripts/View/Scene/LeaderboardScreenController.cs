using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Scene entry point for the Leaderboard scene. Manages tabs, sorting, entry list,
/// context menu, and auto-scroll from victory screen.
/// </summary>
public sealed class LeaderboardScreenController : NavigableScene
{
    // Tab definitions: name → (width, height) or (0,0) for All
    private static readonly (string name, int w, int h)[] Tabs =
    {
        ("tab-small", 10, 10),
        ("tab-medium", 20, 20),
        ("tab-large", 40, 40),
        ("tab-xlarge", 100, 100),
        ("tab-all", 0, 0),
    };

    // Using Root from NavigableScene base class.

    private static readonly string[] TabLabelsFull =
    {
        "Small",
        "Medium",
        "Large",
        "XLarge",
        "All",
    };
    private static readonly string[] TabLabelsShort = { "S", "M", "L", "XL", "All" };

    private const float NarrowTabBarThreshold = 420f;
    private VisualElement _list;
    private ScrollView _scroll;
    private Label _emptyLabel;
    private VisualElement _comingSoon;
    private VisualElement _playerPanel;
    private Label _playerPanelLabel;
    private Button _playerPlayBtn;
    private string _playerGameId;
    private Button _refreshBtn;
    private VisualElement _toast;
    private Label _toastText;

    private VisualElement _tabBar;
    private Button[] _tabButtons;
    private Button[] _sortButtons;

    // Mode-tab state (Classic vs Endless). Mirrors the singleplayer screen.
    private Button _modeClassicTab;
    private Button _modeEndlessTab;
    private VisualElement _classicSizeTabs;
    private VisualElement _endlessSizeTabs;
    private Button[] _endlessTabButtons;
    private Button _endlessRefreshBtn;
    private LeaderboardMode _activeMode = LeaderboardMode.Classic;
    private int _activeEndlessTabIndex;

    /// <summary>
    /// Endless leaderboard size presets. Mirrors the singleplayer endless
    /// preset row (5×5 / 10×10 / 20×20). No "All" tab — endless rankings
    /// are per-config; cross-config aggregation isn't meaningful for the
    /// time-pressured run length.
    /// </summary>
    private static readonly (string name, int w, int h)[] EndlessTabs =
    {
        ("lb-endless-tab-small", 5, 5),
        ("lb-endless-tab-medium", 10, 10),
        ("lb-endless-tab-large", 20, 20),
    };

    // Cache of fetched endless leaderboards, indexed by EndlessTabs position.
    private readonly (
        EndlessGlobalLeaderboardResponse lb,
        EndlessPlayerEntryResponse me
    )?[] _endlessCache = new (EndlessGlobalLeaderboardResponse, EndlessPlayerEntryResponse)?[
        EndlessTabs.Length
    ];

    // Nullable so BuildUI can reset it on scene re-enable, forcing
    // OnTabBarGeometryChanged to re-apply labels to the freshly-recreated
    // tab buttons (which always carry the full labels from UXML).
    private bool? _usingShortLabels;

    private int _activeTabIndex;
    private SortCriterion _activeSortCriterion = SortCriterion.Fastest;
    private bool _isGlobalView;

    // Context menu state
    private LeaderboardContextMenu _contextMenu;

    // Compact mode — hides inline fav/play buttons on narrow screens.
    // Derived from the live class list (no caching) so it stays correct
    // across scene re-enables, where the visual tree is recreated but
    // C# fields persist.
    private const string CompactClass = "lb-screen--compact";
    private bool _isCompact => Root != null && Root.ClassListContains(CompactClass);

    // Drag-to-scroll state
    private bool _isDragScrolling;
    private bool _dragPending;
    private float _dragScrollStartY;
    private float _dragScrollStartValue;
    private Vector2 _dragStartPosition;
    private float _dragThreshold;

    // Entry selection
    private VisualElement _selectedRow;

    // Global leaderboard cache — avoids re-fetching on every tab switch
    private readonly (GlobalLeaderboardResponse lb, PlayerEntryResponse me)?[] _globalCache = new (
        GlobalLeaderboardResponse,
        PlayerEntryResponse
    )?[5];

    // Focus (auto-scroll) state from victory screen
    private string _focusGameId;

    // Keyboard navigation
    // Using Navigator from NavigableScene base class.
    private int _navTabsStart;
    private int _navSortStart;
    private int _navEntriesStart;
    private VisualElement _focusAfterRebuild;
    private string _focusGameIdAfterRebuild;
    private string _focusBtnClassAfterRebuild;
    private int _focusEntryPositionAfterRebuild = -1;

    /// <summary>
    /// Per-row builder for classic-leaderboard entries. Created once in
    /// <see cref="BuildUI"/> with controller-side callbacks; reused for
    /// every list rebuild.
    /// </summary>
    private LeaderboardEntryRow _entryRow;
    private LeaderboardGlobalEntryRow _globalEntryRow;

    protected override KeybindManager.Context NavContext => KeybindManager.Context.Leaderboard;

    protected override void BuildUI(VisualElement root)
    {
        _usingShortLabels = null;

        _dragThreshold = PlayerPrefs.GetFloat(
            GameSettings.DragThresholdPrefKey,
            GameSettings.DefaultDragThreshold
        );

        _list = root.Q("lb-list");
        _scroll = root.Q<ScrollView>("lb-scroll");
        _emptyLabel = root.Q<Label>("lb-empty");
        _comingSoon = root.Q("lb-coming-soon");

        root.Q<Button>("lb-back-btn").clicked += OnBack;

        var localBtn = root.Q<Button>("lb-local-btn");
        var globalBtn = root.Q<Button>("lb-global-btn");
        localBtn.clicked += () => SetScope(false, localBtn, globalBtn);
        globalBtn.clicked += () => SetScope(true, localBtn, globalBtn);

        // Load the persisted local/global preference (defaults to local on
        // first run). The full visibility state for it is applied in
        // BuildNavGraph after _list and friends are populated, so SetScope
        // can safely fire its refresh.
        _isGlobalView =
            PlayerPrefs.GetInt(GameSettings.LeaderboardGlobalViewPrefKey, defaultValue: 0) == 1;

        // Mode tabs (Classic vs Endless). Each swaps the size-tab row +
        // the data fetched / rendered. Classic stays the source-of-truth
        // path with full sort + favorites + context-menu support; endless
        // is a simpler global-only view (rank by clears desc, duration
        // asc tiebreak — no sort options, no local store yet).
        _modeClassicTab = root.Q<Button>("lb-mode-classic");
        _modeEndlessTab = root.Q<Button>("lb-mode-endless");
        _classicSizeTabs = root.Q("lb-classic-size-tabs");
        _endlessSizeTabs = root.Q("lb-endless-size-tabs");
        if (_modeClassicTab != null)
            _modeClassicTab.clicked += () => SelectMode(LeaderboardMode.Classic);
        if (_modeEndlessTab != null)
            _modeEndlessTab.clicked += () => SelectMode(LeaderboardMode.Endless);

        // Classic size tabs
        _tabBar = _classicSizeTabs ?? root.Q(className: "tab-bar");
        _tabButtons = new Button[Tabs.Length];
        for (int i = 0; i < Tabs.Length; i++)
        {
            int idx = i;
            _tabButtons[i] = root.Q<Button>(Tabs[i].name);
            _tabButtons[i].clicked += () => SelectTab(idx);
        }
        _tabBar.RegisterCallback<GeometryChangedEvent>(OnTabBarGeometryChanged);

        // Endless size tabs
        _endlessTabButtons = new Button[EndlessTabs.Length];
        for (int i = 0; i < EndlessTabs.Length; i++)
        {
            int idx = i;
            _endlessTabButtons[i] = root.Q<Button>(EndlessTabs[i].name);
            if (_endlessTabButtons[i] != null)
                _endlessTabButtons[i].clicked += () => SelectEndlessTab(idx);
        }
        _endlessRefreshBtn = root.Q<Button>("lb-endless-refresh-btn");
        if (_endlessRefreshBtn != null)
            _endlessRefreshBtn.clicked += () => FetchEndlessGlobalList();

        _sortButtons = new Button[3];
        _sortButtons[0] = root.Q<Button>("sort-fastest");
        _sortButtons[1] = root.Q<Button>("sort-biggest");
        _sortButtons[2] = root.Q<Button>("sort-favorites");
        _sortButtons[0].clicked += () => SelectSort(SortCriterion.Fastest);
        _sortButtons[1].clicked += () => SelectSort(SortCriterion.Biggest);
        _sortButtons[2].clicked += () => SelectSort(SortCriterion.Favorites);

        // Context menu (delete + compact-mode favorite/play). Owns the
        // floating menu element, its delete-confirmation modal, and the
        // popup keyboard nav. Calls back here for the actual mutations.
        _contextMenu = new LeaderboardContextMenu(
            root,
            new LeaderboardContextMenu.Callbacks
            {
                IsCompact = () => _isCompact,
                IsFavorite = id => LeaderboardManager.Instance?.IsFavorite(id) ?? false,
                OnToggleFavorite = OnToggleFavorite,
                OnPlay = OnPlayReplay,
                OnDeleteConfirmed = OnContextMenuDeleteConfirmed,
            }
        );

        // Per-row builder for the classic-leaderboard list. Wires its
        // callbacks to controller-side methods so each row delegates
        // back here for favorite toggles, replay launches, etc.
        _entryRow = new LeaderboardEntryRow(
            new LeaderboardEntryRow.Callbacks
            {
                IsFavoritesSort = () => _activeSortCriterion == SortCriterion.Favorites,
                OnToggleFavorite = OnToggleFavorite,
                OnPlay = OnPlayReplay,
                OnContextMenu = (id, fav, anchor) => _contextMenu.Show(id, fav, anchor),
                RegisterNameScroll = RegisterNameScroll,
            }
        );

        // Per-row builder for global-leaderboard rows (different shape:
        // play button only, no favorite/ctx; highlight tints the viewer's
        // own row).
        _globalEntryRow = new LeaderboardGlobalEntryRow(
            new LeaderboardGlobalEntryRow.Callbacks
            {
                OnPlay = OnPlayGlobalReplay,
                RegisterNameScroll = RegisterNameScroll,
            }
        );

        _playerPanel = root.Q("lb-player-panel");
        _playerPanelLabel = root.Q<Label>("lb-player-panel-label");
        _playerPlayBtn = root.Q<Button>("lb-player-play-btn");
        if (_playerPlayBtn != null)
            _playerPlayBtn.clicked += () => OnPlayGlobalReplay(_playerGameId);
        _refreshBtn = root.Q<Button>("lb-refresh-btn");
        if (_refreshBtn != null)
            _refreshBtn.clicked += () => FetchGlobalList();
        _toast = root.Q("lb-toast");
        _toastText = root.Q<Label>("lb-toast-text");

        if (_playerPanelLabel != null)
            _playerPanelLabel.RegisterCallback<PointerDownEvent>(_ =>
            {
                if (_playerPanelLabel.ClassListContains("lb-player-panel-label--link"))
                    SettingsController.Instance?.Open();
            });

        root.RegisterCallback<PointerDownEvent>(OnRootPointerDown);
        _scroll.RegisterCallback<WheelEvent>(_ => _contextMenu?.Dismiss());
        _scroll.verticalScroller.valueChanged += _ => _contextMenu?.Dismiss();

        _scroll.RegisterCallback<PointerDownEvent>(OnScrollPointerDown);
        _scroll.RegisterCallback<PointerMoveEvent>(OnScrollPointerMove);
        _scroll.RegisterCallback<PointerUpEvent>(OnScrollPointerUp);
        _scroll.RegisterCallback<PointerCaptureOutEvent>(_ =>
        {
            _isDragScrolling = false;
            _dragPending = false;
        });

        root.RegisterCallback<GeometryChangedEvent>(OnRootGeometryChanged);
    }

    protected override void BuildNavGraph(FocusNavigator nav)
    {
        // Apply the persisted local/global toggle now that the full visual
        // tree (including _list, _refreshBtn, _playerPanel, _sortButtons) is
        // populated. SetScope handles button-active classes, visibility, and
        // the initial fetch on the global path.
        var localBtn = Root.Q<Button>("lb-local-btn");
        var globalBtn = Root.Q<Button>("lb-global-btn");
        SetScope(_isGlobalView, localBtn, globalBtn);

        // Handle auto-scroll from victory.
        _focusGameId = GameSettings.LeaderboardFocusGameId;
        GameSettings.LeaderboardFocusGameId = null;

        if (_focusGameId != null)
            AutoScrollToFocusEntry();
        else
            SelectTab(0);

        // RebuildEntryNavigator (called by SelectTab) handles the actual nav graph.
    }

    protected override bool PreUpdate(KeybindManager km)
    {
        // Context menu open: it handles its own navigation.
        if (_contextMenu != null && _contextMenu.IsKeyboardNavActive)
        {
            _contextMenu.UpdateKeyboardNav();
            if (!_contextMenu.IsKeyboardNavActive && Navigator != null)
                Navigator.SuppressDAS();
            return false; // Skip Navigator.Update() this frame.
        }
        return true;
    }

    protected override void OnUpdate(KeybindManager km)
    {
        if (km.TabSmall.WasPerformedThisFrame())
            SelectTab(0);
        else if (km.TabMedium.WasPerformedThisFrame())
            SelectTab(1);
        else if (km.TabLarge.WasPerformedThisFrame())
            SelectTab(2);
        else if (km.TabXLarge.WasPerformedThisFrame())
            SelectTab(3);
        else if (km.TabAll.WasPerformedThisFrame())
            SelectTab(4);

        if (km.ToggleFavorites.WasPerformedThisFrame())
        {
            if (_activeSortCriterion == SortCriterion.Favorites)
                SelectSort(SortCriterion.Fastest);
            else
                SelectSort(SortCriterion.Favorites);
        }

        if (km.SwapGlobal.WasPerformedThisFrame())
        {
            var localBtn = Root.Q<Button>("lb-local-btn");
            var globalBtn = Root.Q<Button>("lb-global-btn");
            SetScope(!_isGlobalView, localBtn, globalBtn);
        }

        if (
            _isGlobalView
            && UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.rKey.wasPressedThisFrame
            && !km.TextFieldFocused
        )
            FetchGlobalList();
    }

    protected override void OnCancel() => OnBack();

    private const float CompactWidthThreshold = 500f;

    private void OnRootGeometryChanged(GeometryChangedEvent evt)
    {
        bool compact = evt.newRect.width < CompactWidthThreshold;
        if (compact)
            Root.AddToClassList(CompactClass);
        else
            Root.RemoveFromClassList(CompactClass);
    }

    private void AutoScrollToFocusEntry()
    {
        var manager = LeaderboardManager.Instance;
        if (manager == null)
        {
            SelectTab(0);
            return;
        }

        // Find the entry to determine which tab to select
        LeaderboardEntry focusEntry = null;
        foreach (var entry in manager.Store.Entries)
        {
            if (entry.gameId == _focusGameId)
            {
                focusEntry = entry;
                break;
            }
        }

        if (focusEntry == null)
        {
            SelectTab(0);
            return;
        }

        // Find the matching tab
        int targetTab = Tabs.Length - 1; // default to All
        for (int i = 0; i < Tabs.Length - 1; i++)
        {
            if (Tabs[i].w == focusEntry.boardWidth && Tabs[i].h == focusEntry.boardHeight)
            {
                targetTab = i;
                break;
            }
        }

        SelectTab(targetTab);

        // Schedule scroll after layout resolves
        _scroll.schedule.Execute(() => ScrollToFocusEntry()).ExecuteLater(50);
    }

    private void ScrollToFocusEntry()
    {
        if (_focusGameId == null)
            return;

        // Find the focused row element and scroll to it
        foreach (var child in _list.Children())
        {
            if (child.userData is string gameId && gameId == _focusGameId)
            {
                _scroll.ScrollTo(child);
                return;
            }
        }
    }

    // --- Responsive tab labels ---

    private void OnTabBarGeometryChanged(GeometryChangedEvent evt)
    {
        bool shouldUseShort = _tabBar.resolvedStyle.width < NarrowTabBarThreshold;
        if (shouldUseShort == _usingShortLabels)
            return;
        _usingShortLabels = shouldUseShort;
        var labels = shouldUseShort ? TabLabelsShort : TabLabelsFull;
        for (int i = 0; i < _tabButtons.Length; i++)
            _tabButtons[i].text = labels[i];
    }

    // --- Tab / Sort selection ---

    private void SelectTab(int index)
    {
        _activeTabIndex = index;
        for (int i = 0; i < _tabButtons.Length; i++)
        {
            if (i == index)
                _tabButtons[i].AddToClassList("tab-bar__tab--active");
            else
                _tabButtons[i].RemoveFromClassList("tab-bar__tab--active");
        }

        _contextMenu?.Dismiss();

        bool isAllTab = Tabs[index].w == 0 && Tabs[index].h == 0;

        // Sort buttons are hidden in global view
        if (!_isGlobalView)
        {
            // Fastest is useless on All (small boards always win); Biggest is useless on size tabs
            ShowElement(_sortButtons[0], !isAllTab); // Fastest
            ShowElement(_sortButtons[1], isAllTab); // Biggest
        }

        // Fall back when the active sort is hidden on this tab
        if (isAllTab && _activeSortCriterion == SortCriterion.Fastest)
            SelectSort(SortCriterion.Biggest);
        else if (!isAllTab && _activeSortCriterion == SortCriterion.Biggest)
            SelectSort(SortCriterion.Fastest);
        else
            RefreshList();
    }

    private void SelectSort(SortCriterion criterion)
    {
        _contextMenu?.Dismiss();
        _activeSortCriterion = criterion;
        int idx = (int)criterion;
        for (int i = 0; i < _sortButtons.Length; i++)
        {
            if (i == idx)
                _sortButtons[i].AddToClassList("filter-row__btn--active");
            else
                _sortButtons[i].RemoveFromClassList("filter-row__btn--active");
        }
        RefreshList();
        _scroll.verticalScroller.value = 0;
    }

    /// <summary>
    /// Switches between Classic and Endless leaderboard modes. Toggles
    /// visibility of the two size-tab rows + the (currently classic-only)
    /// sort row + (currently classic-only) local context menu, and routes
    /// the active fetch path through the appropriate controller methods.
    /// </summary>
    private void SelectMode(LeaderboardMode mode)
    {
        _activeMode = mode;
        _contextMenu?.Dismiss();

        bool isClassic = mode == LeaderboardMode.Classic;
        if (_modeClassicTab != null)
        {
            if (isClassic)
                _modeClassicTab.AddToClassList("tab-bar__tab--active");
            else
                _modeClassicTab.RemoveFromClassList("tab-bar__tab--active");
        }
        if (_modeEndlessTab != null)
        {
            if (!isClassic)
                _modeEndlessTab.AddToClassList("tab-bar__tab--active");
            else
                _modeEndlessTab.RemoveFromClassList("tab-bar__tab--active");
        }

        ShowElement(_classicSizeTabs, isClassic);
        ShowElement(_endlessSizeTabs, !isClassic);

        // Sort + favorites + context menu are classic-only for now.
        var sortRow = Root?.Q(className: "filter-row");
        ShowElement(sortRow, isClassic);

        if (isClassic)
            RefreshList();
        else
            SelectEndlessTab(_activeEndlessTabIndex);
    }

    private void SelectEndlessTab(int index)
    {
        _activeEndlessTabIndex = index;
        for (int i = 0; i < _endlessTabButtons.Length; i++)
        {
            if (_endlessTabButtons[i] == null)
                continue;
            if (i == index)
                _endlessTabButtons[i].AddToClassList("tab-bar__tab--active");
            else
                _endlessTabButtons[i].RemoveFromClassList("tab-bar__tab--active");
        }
        if (_isGlobalView)
        {
            ShowElement(_endlessRefreshBtn, true);
            RefreshEndlessGlobalList();
        }
        else
        {
            // Local endless leaderboard isn't tracked yet — show the empty
            // state with a hint that runs are server-tracked.
            ShowElement(_endlessRefreshBtn, false);
            _list.Clear();
            ShowElement(_scroll, false);
            _emptyLabel.text =
                "Local endless leaderboards aren't tracked yet.\n"
                + "Switch to Global to view your submitted runs.";
            ShowElement(_emptyLabel, true);
            ShowElement(_playerPanel, false);
        }
    }

    private void RefreshEndlessGlobalList()
    {
        var cached = _endlessCache[_activeEndlessTabIndex];
        if (cached.HasValue)
        {
            PopulateEndlessGlobalList(cached.Value.lb, cached.Value.me);
            return;
        }
        FetchEndlessGlobalList();
    }

    private async void FetchEndlessGlobalList()
    {
        _list.Clear();
        ShowEmpty(false);
        ShowElement(_scroll, true);
        _emptyLabel.text = "Loading...";
        ShowElement(_emptyLabel, true);

        var api = new ApiClient();
        var (w, h) = (EndlessTabs[_activeEndlessTabIndex].w, EndlessTabs[_activeEndlessTabIndex].h);
        int tabAtFetch = _activeEndlessTabIndex;

        var lbTask = api.GetEndlessLeaderboardAsync(w, h);
        System.Threading.Tasks.Task<ApiResult<EndlessPlayerEntryResponse>> meTask = null;
        if (api.IsLoggedIn)
            meTask = api.GetEndlessPlayerEntryAsync(w, h);

        var lbResult = await lbTask;
        if (_activeMode != LeaderboardMode.Endless || !_isGlobalView)
            return;
        if (!lbResult.Success)
        {
            ShowElement(_scroll, false);
            _emptyLabel.text = DescribeApiError(lbResult.StatusCode, lbResult.Error);
            ShowElement(_emptyLabel, true);
            ShowElement(_playerPanel, false);
            return;
        }

        EndlessPlayerEntryResponse meResult = null;
        if (meTask != null)
        {
            var meApiResult = await meTask;
            if (_activeMode != LeaderboardMode.Endless || !_isGlobalView)
                return;
            if (meApiResult.Success)
                meResult = meApiResult.Data;
        }

        _endlessCache[tabAtFetch] = (lbResult.Data, meResult);
        PopulateEndlessGlobalList(lbResult.Data, meResult);
    }

    private void PopulateEndlessGlobalList(
        EndlessGlobalLeaderboardResponse lb,
        EndlessPlayerEntryResponse me
    )
    {
        _list.Clear();
        if (lb == null || lb.entries == null || lb.entries.Length == 0)
        {
            ShowElement(_scroll, false);
            _emptyLabel.text = "No endless runs submitted yet.";
            ShowElement(_emptyLabel, true);
            ShowElement(_playerPanel, false);
            return;
        }

        ShowElement(_scroll, true);
        ShowElement(_emptyLabel, false);

        bool isAllTab = EndlessTabs[_activeEndlessTabIndex].w == 0;
        foreach (var e in lb.entries)
        {
            var row = new VisualElement();
            row.AddToClassList("lb-entry");
            var rankLabel = new Label($"#{e.rank}");
            rankLabel.AddToClassList("lb-entry__rank");
            row.Add(rankLabel);

            var nameLabel = new Label(e.displayName ?? "");
            nameLabel.AddToClassList("lb-entry__name");
            row.Add(nameLabel);

            var statsLabel = new Label(FormatEndlessStats(e, isAllTab));
            statsLabel.AddToClassList("lb-entry__time");
            row.Add(statsLabel);

            _list.Add(row);
        }

        // Player panel: show the viewer's rank if logged in + present in the
        // table. Reuses the classic player-panel slot for visual consistency.
        if (me != null && _playerPanel != null)
        {
            ShowElement(_playerPanel, true);
            if (_playerPanelLabel != null)
                _playerPanelLabel.text =
                    $"You: #{me.rank} of {me.totalEntries} — {me.clears} clears in {FormatDuration(me.durationSeconds)}";
        }
        else
        {
            ShowElement(_playerPanel, false);
        }
    }

    private static string FormatEndlessStats(EndlessGlobalLeaderboardEntry e, bool includeBoardSize)
    {
        string boardSuffix =
            includeBoardSize && e.boardWidth > 0 && e.boardHeight > 0
                ? $" · {e.boardWidth}×{e.boardHeight}"
                : "";
        return $"{e.clears} clears · {FormatDuration(e.durationSeconds)}{boardSuffix}";
    }

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s" : $"{seconds:F1}s";
    }

    private void SetScope(bool isGlobal, Button localBtn, Button globalBtn)
    {
        _isGlobalView = isGlobal;
        // Persist for next session — the toggle outlives this scene.
        PlayerPrefs.SetInt(GameSettings.LeaderboardGlobalViewPrefKey, isGlobal ? 1 : 0);
        PlayerPrefs.Save();
        if (isGlobal)
        {
            globalBtn.AddToClassList("toggle-group__btn--active");
            localBtn.RemoveFromClassList("toggle-group__btn--active");
            ShowElement(_comingSoon, false);
            ShowElement(_playerPanel, true);

            if (_activeMode == LeaderboardMode.Classic)
            {
                ShowElement(_refreshBtn, true);
                ShowElement(_endlessRefreshBtn, false);
                foreach (var btn in _sortButtons)
                    ShowElement(btn, false);
                RefreshGlobalList();
            }
            else
            {
                ShowElement(_refreshBtn, false);
                ShowElement(_endlessRefreshBtn, true);
                foreach (var btn in _sortButtons)
                    ShowElement(btn, false);
                RefreshEndlessGlobalList();
            }
        }
        else
        {
            localBtn.AddToClassList("toggle-group__btn--active");
            globalBtn.RemoveFromClassList("toggle-group__btn--active");
            ShowElement(_comingSoon, false);
            ShowElement(_refreshBtn, false);
            ShowElement(_endlessRefreshBtn, false);
            ShowElement(_playerPanel, false);

            if (_activeMode == LeaderboardMode.Classic)
            {
                bool isAllTab = Tabs[_activeTabIndex].w == 0 && Tabs[_activeTabIndex].h == 0;
                ShowElement(_sortButtons[0], !isAllTab);
                ShowElement(_sortButtons[1], isAllTab);
                ShowElement(_sortButtons[2], true);
                RefreshList();
            }
            else
            {
                foreach (var btn in _sortButtons)
                    ShowElement(btn, false);
                SelectEndlessTab(_activeEndlessTabIndex);
            }
        }
    }

    // --- List population ---

    private void RefreshList()
    {
        _selectedRow = null;

        if (_isGlobalView)
        {
            RefreshGlobalList();
            return;
        }

        var manager = LeaderboardManager.Instance;
        if (manager == null)
        {
            ShowEmpty(true);
            return;
        }

        var (w, h) = (Tabs[_activeTabIndex].w, Tabs[_activeTabIndex].h);
        bool isAllTab = w == 0 && h == 0;

        List<LeaderboardEntry> entries = isAllTab
            ? manager.Store.GetAllEntries()
            : manager.Store.GetEntries(w, h);

        entries = LeaderboardStore.SortBy(entries, _activeSortCriterion);

        _list.Clear();

        if (entries.Count == 0)
        {
            _emptyLabel.text = "No scores yet. Play a game to see your times here!";
            ShowEmpty(true);
            RebuildEntryNavigator();
            return;
        }

        ShowEmpty(false);

        // Medal highlights for top 3 in current sort (skip in Favorites sort)
        HashSet<string> gold = null,
            silver = null,
            bronze = null;
        if (_activeSortCriterion != SortCriterion.Favorites)
        {
            if (entries.Count > 0)
                gold = new HashSet<string> { entries[0].gameId };
            if (entries.Count > 1)
                silver = new HashSet<string> { entries[1].gameId };
            if (entries.Count > 2)
                bronze = new HashSet<string> { entries[2].gameId };
        }

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var row = _entryRow.Build(
                i + 1,
                entry,
                isAllTab,
                new LeaderboardEntryRow.Medals(gold, silver, bronze)
            );
            _list.Add(row);
        }

        RebuildEntryNavigator();
    }

    private void ShowEmpty(bool show)
    {
        ShowElement(_emptyLabel, show);
        ShowElement(_scroll, !show);
    }

    // --- Drag-to-scroll ---

    private void OnScrollPointerDown(PointerDownEvent evt)
    {
        _contextMenu?.Dismiss();

        // Let row buttons handle their own pointer events
        if (IsRowButton(evt.target as VisualElement))
            return;

        _dragStartPosition = evt.position;
        _dragScrollStartY = evt.position.y;
        _dragScrollStartValue = _scroll.verticalScroller.value;
        _isDragScrolling = false;

        // Only enable drag-scroll when content overflows
        if (_scroll.verticalScroller.highValue > 0)
        {
            _dragPending = true;
            _scroll.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        else
        {
            _dragPending = true;
        }
    }

    private void OnScrollPointerMove(PointerMoveEvent evt)
    {
        if (!_dragPending && !_isDragScrolling)
            return;

        if (_dragPending && !_isDragScrolling)
        {
            float delta = Mathf.Abs(evt.position.y - _dragScrollStartY);
            if (delta > _dragThreshold)
            {
                _isDragScrolling = true;
                _dragPending = false;
            }
            else
            {
                return;
            }
        }

        if (_isDragScrolling)
        {
            float scrollDelta = _dragScrollStartY - evt.position.y;
            _scroll.verticalScroller.value = _dragScrollStartValue + scrollDelta;
        }
    }

    private void OnScrollPointerUp(PointerUpEvent evt)
    {
        bool wasDragging = _isDragScrolling;
        bool wasPending = _dragPending;

        _isDragScrolling = false;
        _dragPending = false;
        _scroll.ReleasePointer(evt.pointerId);

        if (wasPending && !wasDragging)
        {
            // Was a tap — select the entry
            SelectEntryAtPosition(_dragStartPosition);
        }
    }

    private static bool IsRowButton(VisualElement target)
    {
        while (target != null)
        {
            if (target is Button btn && btn.ClassListContains("lb-row-btn"))
                return true;
            target = target.parent;
        }
        return false;
    }

    private void OnRootPointerDown(PointerDownEvent evt)
    {
        if (_contextMenu == null || !_contextMenu.IsOpen)
            return;

        // Click inside the menu itself doesn't dismiss it.
        if (_contextMenu.ContainsWorldPoint(evt.position))
            return;

        _contextMenu.Dismiss();
    }

    private void OnToggleFavorite(string gameId, bool currentlyFavorite)
    {
        var manager = LeaderboardManager.Instance;
        if (manager != null)
            manager.SetFavorite(gameId, !currentlyFavorite);

        // After rebuild, focus the toggled entry's fav button and scroll to it.
        _focusGameIdAfterRebuild = gameId;
        _focusBtnClassAfterRebuild = "lb-fav-btn";
        RefreshList();
    }

    /// <summary>
    /// Fires when the context menu's delete flow has confirmed (either
    /// immediately for non-favorited entries or after the modal Confirmed
    /// for favorited ones). The controller mutates the store and stages
    /// the post-rebuild focus restoration.
    /// </summary>
    private void OnContextMenuDeleteConfirmed(string gameId)
    {
        // Track position so focus lands on the replacement entry after
        // the list rebuilds — same row position now holds whatever entry
        // moved up to fill the gap.
        _focusEntryPositionAfterRebuild = FindEntryPosition(gameId);
        _focusBtnClassAfterRebuild = "lb-ctx-trigger";

        var manager = LeaderboardManager.Instance;
        if (manager != null)
            manager.RemoveEntry(gameId);
        RefreshList();
    }

    private int FindEntryPosition(string gameId)
    {
        int pos = 0;
        foreach (var child in _list.Children())
        {
            if (child.userData as string == gameId)
                return pos;
            pos++;
        }
        return -1;
    }

    // --- Replay launch ---

    private void OnPlayReplay(string gameId)
    {
        var manager = LeaderboardManager.Instance;
        if (manager == null)
            return;

        var replay = manager.LoadReplay(gameId);
        if (replay == null)
        {
            Debug.LogWarning($"LeaderboardScreen: replay not found for {gameId}");
            return;
        }

        GameSettings.StartReplay(replay);
        SceneNav.Push("ReplayViewer");
    }

    // --- Global leaderboard ---

    /// <summary>
    /// Shows global leaderboard data. Uses cached data if available; fetches from server otherwise.
    /// Called on tab switch and scope change.
    /// </summary>
    private void RefreshGlobalList()
    {
        var cached = _globalCache[_activeTabIndex];
        if (cached.HasValue)
        {
            PopulateGlobalList(cached.Value.lb, cached.Value.me);
            return;
        }

        FetchGlobalList();
    }

    /// <summary>
    /// Forces a fresh fetch from the server, ignoring cache. Called by the refresh button.
    /// </summary>
    private async void FetchGlobalList()
    {
        _list.Clear();
        ShowEmpty(false);
        ShowElement(_scroll, true);

        // Show loading state
        _emptyLabel.text = "Loading...";
        ShowElement(_emptyLabel, true);

        var api = new ApiClient();
        var (w, h) = (Tabs[_activeTabIndex].w, Tabs[_activeTabIndex].h);
        bool isAllTab = w == 0 && h == 0;
        int tabAtFetch = _activeTabIndex;

        // Fetch leaderboard and player entry in parallel
        var lbTask = isAllTab ? api.GetLeaderboardAllAsync() : api.GetLeaderboardAsync(w, h);
        System.Threading.Tasks.Task<ApiResult<PlayerEntryResponse>> meTask = null;
        if (api.IsLoggedIn)
            meTask = isAllTab ? api.GetPlayerEntryAllAsync() : api.GetPlayerEntryAsync(w, h);

        var lbResult = await lbTask;

        // User may have switched away from Global while awaiting — discard stale results
        if (!_isGlobalView)
            return;

        if (!lbResult.Success)
        {
            string errorMsg = DescribeApiError(lbResult.StatusCode, lbResult.Error);
            ShowElement(_scroll, false);
            _emptyLabel.text = errorMsg;
            ShowElement(_emptyLabel, true);
            ShowElement(_playerPanel, false);
            return;
        }

        PlayerEntryResponse meResult = null;
        if (meTask != null)
        {
            var meApiResult = await meTask;
            if (!_isGlobalView)
                return;
            if (meApiResult.Success)
                meResult = meApiResult.Data;
        }

        // Cache the result for this tab
        _globalCache[tabAtFetch] = (lbResult.Data, meResult);

        PopulateGlobalList(lbResult.Data, meResult);
    }

    private void PopulateGlobalList(GlobalLeaderboardResponse lb, PlayerEntryResponse me)
    {
        var api = new ApiClient();
        bool isAllTab = Tabs[_activeTabIndex].w == 0 && Tabs[_activeTabIndex].h == 0;

        _list.Clear();

        if (lb.entries == null || lb.entries.Length == 0)
        {
            _emptyLabel.text = "No scores yet — be the first!";
            ShowEmpty(true);
        }
        else
        {
            ShowElement(_emptyLabel, false);
            ShowElement(_scroll, true);

            string highlightGameId = me?.gameId;
            foreach (var entry in lb.entries)
            {
                var row = _globalEntryRow.Build(entry, isAllTab, entry.gameId == highlightGameId);
                _list.Add(row);
            }
        }

        RebuildEntryNavigator();
        UpdatePlayerPanel(lb, me, api);
    }

    private void UpdatePlayerPanel(
        GlobalLeaderboardResponse lb,
        PlayerEntryResponse me,
        ApiClient api
    )
    {
        if (_playerPanel == null || _playerPanelLabel == null)
            return;

        ShowElement(_playerPanel, true);

        if (lb == null)
        {
            // Caller already showed the error in the empty label; hide the player panel.
            ShowElement(_playerPanel, false);
            return;
        }

        if (!api.IsLoggedIn)
        {
            _playerPanelLabel.text = "Register or log in to appear on the global leaderboard.";
            _playerPanelLabel.AddToClassList("lb-player-panel-label--link");
            ShowElement(_playerPlayBtn, false);
            return;
        }
        _playerPanelLabel.RemoveFromClassList("lb-player-panel-label--link");

        if (me == null)
        {
            var (w, h) = (Tabs[_activeTabIndex].w, Tabs[_activeTabIndex].h);
            bool isAllTab = w == 0 && h == 0;
            _playerPanelLabel.text = isAllTab
                ? "No scores yet. Play a game to enter the leaderboard."
                : "No scores yet for this board size. Play a game to enter the leaderboard.";
            ShowElement(_playerPlayBtn, false);
            return;
        }

        if (me.flagged)
        {
            _playerPanelLabel.text = "Account flagged. Contact support.";
            ShowElement(_playerPlayBtn, false);
            return;
        }

        _playerPanelLabel.text =
            $"Your best: #{me.rank} of {me.totalEntries} \u00B7 {LeaderboardFormatters.FormatTime(me.time)}";
        _playerGameId = me.gameId;
        ShowElement(_playerPlayBtn, true);
    }

    private async void OnPlayGlobalReplay(string gameId)
    {
        if (string.IsNullOrEmpty(gameId))
            return;

        // Check local storage first — avoids re-fetching and has snapshot for non-top-50
        var manager = LeaderboardManager.Instance;
        if (manager != null)
        {
            var local = manager.LoadReplay(gameId);
            if (local != null && !string.IsNullOrEmpty(local.boardSnapshot))
            {
                GameSettings.StartReplay(local);
                SceneNav.Push("ReplayViewer");
                return;
            }
        }

        var api = new ApiClient();
        var result = await api.GetReplayAsync(gameId);
        if (!result.Success || result.Data == null)
        {
            string msg =
                result.StatusCode == 404
                    ? "Replay not found on server."
                    : DescribeApiError(result.StatusCode, result.Error);
            Debug.LogWarning($"[LeaderboardScreen] Failed to fetch replay for {gameId}: {msg}");
            ShowToast(msg);
            return;
        }

        // Server stores top-50 snapshots as gzip-base64 strings.
        // Decompress back to the array before deserializing into ReplayData.
        var replayJson = LeaderboardReplayDecompress.DecompressSnapshotIfNeeded(
            result.Data.replayJson
        );
        var replay = Newtonsoft.Json.JsonConvert.DeserializeObject<ReplayData>(replayJson);
        if (replay == null)
        {
            Debug.LogWarning($"[LeaderboardScreen] Failed to deserialize replay for {gameId}");
            return;
        }

        GameSettings.StartReplay(replay);
        SceneNav.Push("ReplayViewer");
    }

    // --- Navigation ---

    private void OnBack()
    {
        SceneNav.Pop();
    }

    /// <summary>
    /// Append a Button-shaped FocusItem to <paramref name="items"/> with
    /// the given activate handler, returning its index. Wraps the
    /// `OnActivate = () => { handler(); return true; }` boilerplate that
    /// otherwise repeats for every nav target in this scene's graph.
    /// </summary>
    private static int AddNavItem(
        List<FocusNavigator.FocusItem> items,
        VisualElement element,
        Action onActivate
    )
    {
        int idx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = element,
                OnActivate = () =>
                {
                    onActivate();
                    return true;
                },
            }
        );
        return idx;
    }

    /// <summary>
    /// Dispatch for a click on one of an entry row's inline action
    /// buttons. The same button class encodes which action runs (favorite
    /// toggle / context menu open / replay launch — local or global).
    /// Pulled out of the per-row OnActivate lambda so the row-construction
    /// loop stays readable.
    /// </summary>
    private void HandleEntryButtonClick(Button btn, string gameId)
    {
        if (btn.ClassListContains("lb-fav-btn"))
        {
            _focusGameIdAfterRebuild = gameId;
            _focusBtnClassAfterRebuild = "lb-fav-btn";
            OnToggleFavorite(gameId, btn.Q(className: "lb-fav-icon--on") != null);
        }
        else if (btn.ClassListContains("lb-ctx-trigger"))
        {
            _contextMenu.Show(
                gameId,
                btn.parent.Q(className: "lb-fav-icon--on") != null,
                btn.parent
            );
        }
        else if (_isGlobalView)
        {
            OnPlayGlobalReplay(gameId);
        }
        else
        {
            OnPlayReplay(gameId);
        }
    }

    private void RebuildEntryNavigator()
    {
        Navigator?.Dispose();
        Navigator = new FocusNavigator(Root);

        var items = new List<FocusNavigator.FocusItem>();

        // -- Header row: back button + local/global toggle --
        var backBtn = Root.Q<Button>("lb-back-btn");
        int backIdx = AddNavItem(items, backBtn, OnBack);

        var localBtn = Root.Q<Button>("lb-local-btn");
        var globalBtn = Root.Q<Button>("lb-global-btn");
        int localIdx = AddNavItem(items, localBtn, () => SetScope(false, localBtn, globalBtn));
        int globalIdx = AddNavItem(items, globalBtn, () => SetScope(true, localBtn, globalBtn));

        // -- Mode tabs (Classic | Endless) --
        // Always present so they can carry keyboard focus regardless of which
        // size-tab row is currently visible.
        int modeClassicIdx =
            _modeClassicTab != null
                ? AddNavItem(items, _modeClassicTab, () => SelectMode(LeaderboardMode.Classic))
                : -1;
        int modeEndlessIdx =
            _modeEndlessTab != null
                ? AddNavItem(items, _modeEndlessTab, () => SelectMode(LeaderboardMode.Endless))
                : -1;

        // -- Size tabs (varies by active mode) --
        // Endless mode uses a smaller size set (S/M/L/All); classic uses
        // the full S/M/L/XL/All. The hidden array's buttons are not focusable
        // because their parent container has display: none.
        bool isEndlessMode = _activeMode == LeaderboardMode.Endless;
        Button[] activeSizeButtons = isEndlessMode ? _endlessTabButtons : _tabButtons;
        int activeSizeTabCount = activeSizeButtons?.Length ?? 0;
        int tabsStart = items.Count;
        for (int i = 0; i < activeSizeTabCount; i++)
        {
            if (activeSizeButtons[i] == null)
                continue;
            int idx = i;
            AddNavItem(
                items,
                activeSizeButtons[i],
                () =>
                {
                    if (isEndlessMode)
                        SelectEndlessTab(idx);
                    else
                        SelectTab(idx);
                }
            );
        }
        int tabsEnd = items.Count - 1;
        int sizeTabCount = items.Count - tabsStart;

        // Refresh button (global view only, sits next to last tab).
        int refreshIdx = -1;
        if (_isGlobalView && _refreshBtn != null && !_refreshBtn.ClassListContains("lb--hidden"))
            refreshIdx = AddNavItem(items, _refreshBtn, FetchGlobalList);

        // -- Sort buttons (local view only) --
        int sortStart = items.Count;
        int sortCount = 0;
        if (!_isGlobalView)
        {
            for (int i = 0; i < _sortButtons.Length; i++)
            {
                if (_sortButtons[i].ClassListContains("lb--hidden"))
                    continue;
                var sortBtn = _sortButtons[i];
                int si = i;
                AddNavItem(
                    items,
                    sortBtn,
                    () =>
                    {
                        _focusAfterRebuild = sortBtn;
                        SelectSort((SortCriterion)si);
                    }
                );
                sortCount++;
            }
        }

        // -- Entry rows --
        int entriesStart = items.Count;
        int entryCount = 0;
        foreach (var child in _list.Children())
        {
            var row = child;
            string gameId = row.userData as string;

            // Row entry — Enter navigates right to first inline button.
            int rowIdx = items.Count;
            AddNavItem(
                items,
                row,
                () =>
                {
                    if (rowIdx + 1 < items.Count)
                        Navigator.SetFocus(rowIdx + 1);
                }
            );

            // Inline buttons: favorite, play, context menu.
            var rowBtns = row.Query<Button>(className: "lb-row-btn").ToList();
            foreach (var btn in rowBtns)
            {
                var capturedBtn = btn;
                string capturedId = gameId;
                AddNavItem(
                    items,
                    capturedBtn,
                    () => HandleEntryButtonClick(capturedBtn, capturedId)
                );
            }

            entryCount++;
        }

        // Player panel play button (global view, after entries).
        int playerPlayIdx = -1;
        if (
            _isGlobalView
            && _playerPlayBtn != null
            && !_playerPlayBtn.ClassListContains("lb--hidden")
        )
            playerPlayIdx = AddNavItem(
                items,
                _playerPlayBtn,
                () => OnPlayGlobalReplay(_playerGameId)
            );

        _navTabsStart = tabsStart;
        _navSortStart = sortStart;
        _navEntriesStart = entriesStart;

        int activeSizeTabIdx = isEndlessMode ? _activeEndlessTabIndex : _activeTabIndex;
        int initialFocus =
            sizeTabCount > 0 ? tabsStart + Mathf.Clamp(activeSizeTabIdx, 0, sizeTabCount - 1) : 0;
        Navigator.SetItems(items, initialFocus);

        LeaderboardNavLinks.Apply(
            Navigator,
            items,
            new LeaderboardNavLinks.Sections
            {
                BackIdx = backIdx,
                LocalIdx = localIdx,
                GlobalIdx = globalIdx,
                ModeClassicIdx = modeClassicIdx,
                ModeEndlessIdx = modeEndlessIdx,
                TabsStart = tabsStart,
                TabsEnd = tabsEnd,
                SizeTabCount = sizeTabCount,
                RefreshIdx = refreshIdx,
                SortStart = sortStart,
                SortCount = sortCount,
                EntriesStart = entriesStart,
                EntryCount = entryCount,
                PlayerPlayIdx = playerPlayIdx,
                IsEndlessMode = isEndlessMode,
                ActiveTabIndex = _activeTabIndex,
            }
        );

        // Restore focus to a specific element if requested (e.g. after sort/favorite).
        if (_focusAfterRebuild != null)
        {
            for (int i = 0; i < Navigator.ItemCount; i++)
            {
                if (Navigator.GetItemElement(i) == _focusAfterRebuild)
                {
                    Navigator.SetFocus(i);
                    break;
                }
            }
            _focusAfterRebuild = null;
        }
        else if (_focusEntryPositionAfterRebuild >= 0)
        {
            // Focus on the context menu button of the entry at the deleted position.
            // If the position is past the end (deleted last entry), use the new last.
            int targetPos = _focusEntryPositionAfterRebuild;
            int curPos = 0;
            int searchIdx = _navEntriesStart;
            bool found = false;
            while (searchIdx < Navigator.ItemCount)
            {
                var el = Navigator.GetItemElement(searchIdx);
                if (el != null && el.ClassListContains("lb-entry"))
                {
                    if (curPos == targetPos || curPos == entryCount - 1)
                    {
                        int btnCount = el.Query<Button>(className: "lb-row-btn").ToList().Count;
                        for (int bi = 1; bi <= btnCount; bi++)
                        {
                            var btnEl = Navigator.GetItemElement(searchIdx + bi);
                            if (btnEl != null && btnEl.ClassListContains("lb-ctx-trigger"))
                            {
                                Navigator.SetFocus(searchIdx + bi);
                                _scroll.schedule.Execute(() => Navigator.ScrollToFocused());
                                found = true;
                                break;
                            }
                        }
                        if (!found)
                        {
                            Navigator.SetFocus(searchIdx);
                            _scroll.schedule.Execute(() => Navigator.ScrollToFocused());
                        }
                        found = true;
                        break;
                    }
                    curPos++;
                }
                searchIdx++;
            }
            _focusEntryPositionAfterRebuild = -1;
            _focusBtnClassAfterRebuild = null;
        }
        else if (_focusGameIdAfterRebuild != null)
        {
            // Find the entry row with this gameId, then the specific button.
            for (int i = _navEntriesStart; i < Navigator.ItemCount; i++)
            {
                var el = Navigator.GetItemElement(i);
                if (el == null)
                    continue;

                if (
                    _focusBtnClassAfterRebuild != null
                    && el.ClassListContains(_focusBtnClassAfterRebuild)
                )
                {
                    var row = el.parent;
                    if (row != null && row.userData as string == _focusGameIdAfterRebuild)
                    {
                        Navigator.SetFocus(i);
                        // Defer scroll — layout hasn't resolved for newly added elements.
                        _scroll.schedule.Execute(() => Navigator.ScrollToFocused());
                        break;
                    }
                }
            }
            _focusGameIdAfterRebuild = null;
            _focusBtnClassAfterRebuild = null;
        }
    }

    // --- Entry selection ---

    private void SelectEntry(VisualElement row)
    {
        if (_selectedRow == row)
            return;

        if (_selectedRow != null)
        {
            _selectedRow.RemoveFromClassList("lb-entry--selected");
            ResetNameScroll(_selectedRow);
        }

        _selectedRow = row;

        if (row != null)
        {
            row.AddToClassList("lb-entry--selected");
            StartNameScroll(row);
        }
    }

    private void SelectEntryAtPosition(Vector2 position)
    {
        foreach (var child in _list.Children())
        {
            if (child.worldBound.Contains(position))
            {
                SelectEntry(child);
                return;
            }
        }
        SelectEntry(null);
    }

    // --- Name auto-scroll on hover / select ---

    private void RegisterNameScroll(VisualElement wrapper, Label label)
    {
        wrapper.RegisterCallback<PointerEnterEvent>(_ => StartNameScrollLabel(wrapper, label));
        wrapper.RegisterCallback<PointerLeaveEvent>(_ =>
        {
            // Keep scrolled if the entry is selected
            var row = wrapper.parent;
            if (row != null && row.ClassListContains("lb-entry--selected"))
                return;
            ResetNameScrollLabel(label);
        });
    }

    private void StartNameScroll(VisualElement row)
    {
        var wrapper = row.Q(className: "lb-name-wrapper");
        var label = wrapper?.Q<Label>(className: "lb-name");
        if (wrapper != null && label != null)
            StartNameScrollLabel(wrapper, label);
    }

    private void ResetNameScroll(VisualElement row)
    {
        var label = row.Q<Label>(className: "lb-name");
        if (label != null)
            ResetNameScrollLabel(label);
    }

    private static void StartNameScrollLabel(VisualElement wrapper, Label label)
    {
        float textWidth = label.resolvedStyle.width;
        float containerWidth = wrapper.contentRect.width;
        float overflow = textWidth - containerWidth;
        if (overflow <= 0)
            return;

        float duration = Mathf.Max(0.5f, overflow / 60f);
        label.style.transitionDuration = new StyleList<TimeValue>(
            new List<TimeValue> { new TimeValue(duration, TimeUnit.Second) }
        );
        label.style.translate = new Translate(
            new Length(-overflow, LengthUnit.Pixel),
            new Length(0)
        );
    }

    private static void ResetNameScrollLabel(Label label)
    {
        label.style.transitionDuration = new StyleList<TimeValue>(
            new List<TimeValue> { new TimeValue(0.3f, TimeUnit.Second) }
        );
        label.style.translate = new Translate(new Length(0), new Length(0));
    }

    // --- Toast ---

    private void ShowToast(string message, float autoHideSeconds = 0f)
    {
        if (_toast == null || _toastText == null)
            return;
        _toastText.text = message;
        ShowElement(_toast, true);

        if (autoHideSeconds > 0f)
            _toast
                .schedule.Execute(() => ShowElement(_toast, false))
                .ExecuteLater((long)(autoHideSeconds * 1000));
    }

    private void HideToast()
    {
        ShowElement(_toast, false);
    }

    // --- Error descriptions ---

    private static string DescribeApiError(long statusCode, string serverError)
    {
        if (statusCode == 0)
            return "Can't connect to the server.\nScores are only saved locally.";
        if (statusCode == 401)
            return "Session expired. Please log in again.";
        if (statusCode == 429)
            return "Too many requests. Try again later.";
        if (statusCode >= 500)
            return "Server error. Try again later.";
        if (!string.IsNullOrEmpty(serverError) && serverError != "Unknown error")
            return serverError;
        return "Something went wrong. Try again later.";
    }

    private static void ShowElement(VisualElement el, bool show)
    {
        if (el == null)
            return;
        if (show)
            el.RemoveFromClassList("lb--hidden");
        else
            el.AddToClassList("lb--hidden");
    }
}

/// <summary>
/// Top-level mode discriminator for the leaderboard screen. Mirrors the
/// singleplayer selection screen's Classic/Endless tab structure.
/// </summary>
public enum LeaderboardMode
{
    Classic,
    Endless,
}
