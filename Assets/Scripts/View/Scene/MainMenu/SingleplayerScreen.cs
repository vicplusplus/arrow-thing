using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

internal sealed class SingleplayerScreen : MenuScreen
{
    // -- Size select state ------------------------------------------------------

    private const int CustomDimMin = 2;
    private const int CustomDimMax = 400;

    private Button _presetSmall;
    private Button _presetMedium;
    private Button _presetLarge;
    private Button _presetXLarge;
    private Button _presetCustom;
    private VisualElement _customPanel;
    private SnapSlider _customWidthSnap;
    private SnapSlider _customHeightSnap;
    private bool _isCustomSelected;
    private int _selectedWidth = 10;
    private int _selectedHeight = 10;
    private bool _sizeSelectInitialized;

    // Singleplayer mode tabs (Classic | Endless). Each tab shows its own
    // preset grid + start row. Endless has no custom size (run length depends
    // on board fill rate, so non-standard sizes wouldn't be leaderboard-comparable).
    // Endless caps at 20×20 — anything larger doesn't fit one screen with its
    // dependency graph and becomes practically unplayable.
    private Button _spTabClassic;
    private Button _spTabEndless;
    private VisualElement _spClassicPanel;
    private VisualElement _spEndlessPanel;
    private Button _endlessPresetSmall;
    private Button _endlessPresetMedium;
    private Button _endlessPresetLarge;
    private bool _endlessTabActive;
    private int _endlessSelectedSize = 10;
    private const string EndlessSizePrefKey = "endless.size";
    private const string SpTabPrefKey = "menu.sp.tab"; // "classic" | "endless"

    // Nav graph indices (set in BuildNavGraph)
    private int _spStartIdx;
    private int _spPresetBase;
    private int _spBackIdx;
    private int _spLeaderboardIdx;
    private int _spTabClassicIdx;
    private int _spTabEndlessIdx;

    public SingleplayerScreen(MainMenuController owner)
        : base(owner) { }

    protected override string RootElementName => "menu-singleplayer";

    protected override void BuildInternal(VisualElement documentRoot)
    {
        InitSizeSelect(documentRoot);
        documentRoot.Q<Button>("leaderboard-btn").clicked += OnLeaderboard;
        documentRoot.Q<Button>("back-sp-btn").clicked += () =>
            Owner.SetState(MainMenuController.MenuState.Play);

        var continueBtn = documentRoot.Q<Button>("continue-btn");
        if (SaveManager.HasSave())
            SetVisible(continueBtn, true);
        continueBtn.clicked += OnContinue;
    }

    public override void OnUpdate(KeybindManager km)
    {
        if (km.OpenLeaderboard != null && km.OpenLeaderboard.WasPerformedThisFrame())
            OnLeaderboard();
    }

    public override void OnCancel() => Owner.SetState(MainMenuController.MenuState.Play);

    // -- Size select ------------------------------------------------------------

    private void InitSizeSelect(VisualElement root)
    {
        _presetSmall = root.Q<Button>("preset-small");
        _presetMedium = root.Q<Button>("preset-medium");
        _presetLarge = root.Q<Button>("preset-large");
        _presetXLarge = root.Q<Button>("preset-xlarge");
        _presetCustom = root.Q<Button>("preset-custom");
        _customPanel = root.Q("custom-panel");

        _customWidthSnap = new SnapSlider(
            CustomDimMin,
            CustomDimMax,
            20f,
            smallStep: 1f,
            snapStep: 10f,
            format: "0",
            showLock: true
        );
        _customWidthSnap.OnValueChanged += _ => SelectCustom();
        _customPanel.Q("custom-width-row").Add(_customWidthSnap.Root);

        _customHeightSnap = new SnapSlider(
            CustomDimMin,
            CustomDimMax,
            20f,
            smallStep: 1f,
            snapStep: 10f,
            format: "0",
            showLock: true
        );
        _customHeightSnap.OnValueChanged += _ => SelectCustom();
        _customPanel.Q("custom-height-row").Add(_customHeightSnap.Root);

        _presetSmall.clicked += () => SelectPreset(10, 10);
        _presetMedium.clicked += () => SelectPreset(20, 20);
        _presetLarge.clicked += () => SelectPreset(40, 40);
        _presetXLarge.clicked += () => SelectPreset(100, 100);
        _presetCustom.clicked += SelectCustom;
        root.Q<Button>("start-btn").clicked += OnStartGame;

        // Endless tab + presets (Phase 1 of mode tabs). Independent selection
        // state from classic — switching tabs preserves both sides' picks.
        InitEndlessTab(root);

        // Restore selection from GameSettings.
        if (GameSettings.IsSet)
        {
            bool matchesPreset =
                (GameSettings.Width == 10 && GameSettings.Height == 10)
                || (GameSettings.Width == 20 && GameSettings.Height == 20)
                || (GameSettings.Width == 40 && GameSettings.Height == 40)
                || (GameSettings.Width == 100 && GameSettings.Height == 100);

            if (matchesPreset)
            {
                _selectedWidth = GameSettings.Width;
                _selectedHeight = GameSettings.Height;
            }
            else
            {
                _customWidthSnap.SetValueWithoutNotify(GameSettings.Width);
                _customHeightSnap.SetValueWithoutNotify(GameSettings.Height);
                _isCustomSelected = true;
                _selectedWidth = GameSettings.Width;
                _selectedHeight = GameSettings.Height;
                SetVisible(_customPanel, true);
            }
        }
        UpdateAllPresetHighlights();
        _sizeSelectInitialized = true;
    }

    // -- Endless tab ------------------------------------------------------------

    private void InitEndlessTab(VisualElement root)
    {
        _spTabClassic = root.Q<Button>("sp-tab-classic");
        _spTabEndless = root.Q<Button>("sp-tab-endless");
        _spClassicPanel = root.Q("sp-classic-panel");
        _spEndlessPanel = root.Q("sp-endless-panel");

        _endlessPresetSmall = root.Q<Button>("endless-preset-small");
        _endlessPresetMedium = root.Q<Button>("endless-preset-medium");
        _endlessPresetLarge = root.Q<Button>("endless-preset-large");

        _spTabClassic.clicked += () => SetSpTab(endless: false);
        _spTabEndless.clicked += () => SetSpTab(endless: true);

        _endlessPresetSmall.clicked += () => SelectEndlessSize(5);
        _endlessPresetMedium.clicked += () => SelectEndlessSize(10);
        _endlessPresetLarge.clicked += () => SelectEndlessSize(20);

        var endlessBtn = root.Q<Button>("endless-btn");
        if (endlessBtn != null)
            endlessBtn.clicked += OnStartEndless;

        // Restore last-used endless size + tab from prefs. Stored values
        // outside the {5, 10, 20} set (e.g. legacy 16/40) snap back to 10.
        int storedSize = PlayerPrefs.GetInt(EndlessSizePrefKey, 10);
        _endlessSelectedSize =
            (storedSize == 5 || storedSize == 10 || storedSize == 20) ? storedSize : 10;
        UpdateEndlessHighlight();

        bool startOnEndlessTab = PlayerPrefs.GetString(SpTabPrefKey, "classic") == "endless";
        SetSpTab(endless: startOnEndlessTab);
    }

    private void SetSpTab(bool endless)
    {
        bool changed = endless != _endlessTabActive;
        _endlessTabActive = endless;
        PlayerPrefs.SetString(SpTabPrefKey, endless ? "endless" : "classic");

        SetVisible(_spClassicPanel, !endless);
        SetVisible(_spEndlessPanel, endless);

        ToggleClass(_spTabClassic, "tab-bar__tab--active", !endless);
        ToggleClass(_spTabEndless, "tab-bar__tab--active", endless);

        // Rebuild the nav graph: preset items + start row swap with the
        // active tab. Preserve focus where possible (e.g. switching tabs
        // via mouse from a focused preset shouldn't yank focus to nowhere).
        if (changed && _sizeSelectInitialized)
            Owner.RebuildNavigator(preserveFocus: true);
    }

    private void SelectEndlessSize(int size)
    {
        _endlessSelectedSize = size;
        PlayerPrefs.SetInt(EndlessSizePrefKey, size);
        UpdateEndlessHighlight();
        UpdateActiveTabDownLink();
    }

    private void UpdateEndlessHighlight()
    {
        ToggleClass(_endlessPresetSmall, "preset-btn--selected", _endlessSelectedSize == 5);
        ToggleClass(_endlessPresetMedium, "preset-btn--selected", _endlessSelectedSize == 10);
        ToggleClass(_endlessPresetLarge, "preset-btn--selected", _endlessSelectedSize == 20);
    }

    private static void ToggleClass(VisualElement el, string className, bool on)
    {
        if (el == null)
            return;
        if (on)
            el.AddToClassList(className);
        else
            el.RemoveFromClassList(className);
    }

    // -- Nav graph --------------------------------------------------------------

    public override void BuildNavGraph(FocusNavigator nav)
    {
        var items = new List<FocusNavigator.FocusItem>();

        _spBackIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Owner.Root.Q<Button>("back-sp-btn"),
                OnActivate = () =>
                {
                    Owner.SetState(MainMenuController.MenuState.Play);
                    return true;
                },
            }
        );

        _spLeaderboardIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Owner.Root.Q<Button>("leaderboard-btn"),
                OnActivate = () =>
                {
                    OnLeaderboard();
                    return true;
                },
            }
        );

        // Mode tab pair (Classic | Endless). Switching a tab rebuilds the
        // graph, so the OnActivate handlers are safe — focus jumps to the
        // new tab's first preset via RebuildNavigator.
        _spTabClassicIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _spTabClassic,
                OnActivate = () =>
                {
                    SetSpTab(endless: false);
                    return true;
                },
            }
        );
        _spTabEndlessIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _spTabEndless,
                OnActivate = () =>
                {
                    SetSpTab(endless: true);
                    return true;
                },
            }
        );

        _spPresetBase = items.Count;
        if (_endlessTabActive)
            BuildEndlessPresetItems(items);
        else
            BuildClassicPresetItems(items);

        int startIdx = items.Count;
        if (_endlessTabActive)
        {
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = Owner.Root.Q<Button>("endless-btn"),
                    OnActivate = () =>
                    {
                        OnStartEndless();
                        return true;
                    },
                }
            );
        }
        else
        {
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = Owner.Root.Q<Button>("start-btn"),
                    OnActivate = () =>
                    {
                        OnStartGame();
                        return true;
                    },
                }
            );
        }
        _spStartIdx = startIdx;

        int continueIdx = -1;
        var continueBtn = Owner.Root.Q<Button>("continue-btn");
        bool hasContinue = !_endlessTabActive && !continueBtn.ClassListContains("screen--hidden");
        if (hasContinue)
        {
            continueIdx = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = continueBtn,
                    OnActivate = () =>
                    {
                        OnContinue();
                        return true;
                    },
                }
            );
        }

        nav.SetItems(items, GetPresetIndex());

        // Grid links built after layout resolves. Hook the visible grid
        // (only one of the two per-tab grids is in the layout pass at a time).
        var presetGrid = ActivePresetGrid();
        if (presetGrid != null)
        {
            presetGrid.UnregisterCallback<GeometryChangedEvent>(OnPresetGridLayout);
            presetGrid.RegisterCallback<GeometryChangedEvent>(OnPresetGridLayout);
        }

        // Tabs: bidi horizontal pair, plus vertical links to icons above
        // and the preset grid below (filled in by LinkPresetGrid using the
        // resolved row layout).
        nav.LinkBidi(_spTabClassicIdx, FocusNavigator.NavDir.Right, _spTabEndlessIdx);

        // Start / Continue side by side (classic only).
        if (hasContinue)
            nav.LinkBidi(startIdx, FocusNavigator.NavDir.Right, continueIdx);
    }

    private void BuildClassicPresetItems(List<FocusNavigator.FocusItem> items)
    {
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _presetSmall,
                OnActivate = () =>
                {
                    SelectPreset(10, 10);
                    return true;
                },
            }
        );
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _presetMedium,
                OnActivate = () =>
                {
                    SelectPreset(20, 20);
                    return true;
                },
            }
        );
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _presetLarge,
                OnActivate = () =>
                {
                    SelectPreset(40, 40);
                    return true;
                },
            }
        );
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _presetXLarge,
                OnActivate = () =>
                {
                    SelectPreset(100, 100);
                    return true;
                },
            }
        );
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _presetCustom,
                OnActivate = () =>
                {
                    SelectCustom();
                    return true;
                },
            }
        );

        if (_isCustomSelected)
        {
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = _customWidthSnap.Track,
                    CustomFocusVisual = true,
                    OnHorizontal = dir =>
                    {
                        bool shift =
                            UnityEngine.InputSystem.Keyboard.current != null
                            && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;
                        _customWidthSnap.KeyboardStep(dir, shift);
                        return true;
                    },
                }
            );
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = _customHeightSnap.Track,
                    CustomFocusVisual = true,
                    OnHorizontal = dir =>
                    {
                        bool shift =
                            UnityEngine.InputSystem.Keyboard.current != null
                            && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;
                        _customHeightSnap.KeyboardStep(dir, shift);
                        return true;
                    },
                }
            );
        }
    }

    private void BuildEndlessPresetItems(List<FocusNavigator.FocusItem> items)
    {
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _endlessPresetSmall,
                OnActivate = () =>
                {
                    SelectEndlessSize(5);
                    return true;
                },
            }
        );
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _endlessPresetMedium,
                OnActivate = () =>
                {
                    SelectEndlessSize(10);
                    return true;
                },
            }
        );
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = _endlessPresetLarge,
                OnActivate = () =>
                {
                    SelectEndlessSize(20);
                    return true;
                },
            }
        );
    }

    /// <summary>The visible per-tab panel's preset-grid element.</summary>
    private VisualElement ActivePresetGrid()
    {
        var panel = _endlessTabActive ? _spEndlessPanel : _spClassicPanel;
        return panel?.Q(className: "preset-grid");
    }

    private void OnPresetGridLayout(GeometryChangedEvent evt) => LinkPresetGrid();

    private void LinkPresetGrid()
    {
        if (Owner.CurrentState != MainMenuController.MenuState.Singleplayer)
            return;

        var nav = Owner.Navigator;
        nav.ClearLinks();

        // Re-link tab pair (ClearLinks wipes everything, including the bidi
        // we set in BuildNavGraph).
        nav.LinkBidi(_spTabClassicIdx, FocusNavigator.NavDir.Right, _spTabEndlessIdx);

        // Re-link Start/Continue side by side (classic only).
        var continueBtn = Owner.Root.Q<Button>("continue-btn");
        bool hasContinue = !_endlessTabActive && !continueBtn.ClassListContains("screen--hidden");
        if (hasContinue)
        {
            int continueIdx = _spStartIdx + 1;
            nav.LinkBidi(_spStartIdx, FocusNavigator.NavDir.Right, continueIdx);
        }

        int b = _spPresetBase;
        Button[] presets = _endlessTabActive
            ? new[] { _endlessPresetSmall, _endlessPresetMedium, _endlessPresetLarge }
            : new[] { _presetSmall, _presetMedium, _presetLarge, _presetXLarge, _presetCustom };

        var rows = new List<List<int>>();
        float lastY = float.MinValue;
        for (int i = 0; i < presets.Length; i++)
        {
            float y = presets[i].worldBound.y;
            if (rows.Count == 0 || Mathf.Abs(y - lastY) > 10f)
            {
                rows.Add(new List<int>());
                lastY = y;
            }
            rows[rows.Count - 1].Add(b + i);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (int i = 0; i < row.Count - 1; i++)
                nav.LinkBidi(row[i], FocusNavigator.NavDir.Right, row[i + 1]);
            if (r < rows.Count - 1)
            {
                int last = row[row.Count - 1];
                int nextFirst = rows[r + 1][0];
                nav.Link(last, FocusNavigator.NavDir.Right, nextFirst);
                nav.Link(nextFirst, FocusNavigator.NavDir.Left, last);
            }
        }

        for (int r = 0; r < rows.Count - 1; r++)
        {
            var upper = rows[r];
            var lower = rows[r + 1];
            for (int i = 0; i < upper.Count; i++)
            {
                int target = i < lower.Count ? lower[i] : lower[lower.Count - 1];
                nav.LinkBidi(upper[i], FocusNavigator.NavDir.Down, target);
            }
            for (int i = upper.Count; i < lower.Count; i++)
                nav.Link(lower[i], FocusNavigator.NavDir.Up, upper[upper.Count - 1]);
        }

        var lastPresetRow = rows[rows.Count - 1];

        if (_endlessTabActive)
        {
            // Endless: every preset → endless start. Start ↑ goes to the
            // closest preset (tracked column).
            foreach (int idx in lastPresetRow)
                nav.LinkBidi(idx, FocusNavigator.NavDir.Down, _spStartIdx);
            nav.Link(_spStartIdx, FocusNavigator.NavDir.Up, lastPresetRow[lastPresetRow.Count - 1]);
        }
        else
        {
            int customIdx = b + 4;
            int xlargeIdx = b + 3;

            if (_isCustomSelected)
            {
                // Last preset row → first custom slider
                int firstSlider = customIdx + 1;
                foreach (int idx in lastPresetRow)
                    nav.LinkBidi(idx, FocusNavigator.NavDir.Down, firstSlider);
                // Custom slider chain → Start
                nav.LinkChain(customIdx, _spStartIdx - customIdx + 1);
                // Continue ↑ → last slider (height)
                if (hasContinue)
                {
                    int heightSliderIdx = _spStartIdx - 1;
                    nav.Link(_spStartIdx + 1, FocusNavigator.NavDir.Up, heightSliderIdx);
                }
            }
            else
            {
                // Non-Custom presets in last row → Start
                foreach (int idx in lastPresetRow)
                {
                    if (idx != customIdx)
                        nav.LinkBidi(idx, FocusNavigator.NavDir.Down, _spStartIdx);
                }
                // Custom → Continue (if exists), else Start
                int customTarget = hasContinue ? _spStartIdx + 1 : _spStartIdx;
                nav.LinkBidi(customIdx, FocusNavigator.NavDir.Down, customTarget);
                // Start ↑ → XLarge, Continue ↑ → Custom
                nav.Link(_spStartIdx, FocusNavigator.NavDir.Up, xlargeIdx);
                if (hasContinue)
                    nav.Link(_spStartIdx + 1, FocusNavigator.NavDir.Up, customIdx);
            }
        }

        var topRow = rows[0];
        int topLeft = topRow[0];
        int topRight = topRow[topRow.Count - 1];

        // Top of the singleplayer screen behaves as one logical row:
        //   Back ↔ Classic ↔ Endless ↔ Leaderboard
        // Visually Back/Leaderboard are corner icons and the tabs sit
        // centered between them, but for nav purposes Left/Right cycles
        // through all four.
        nav.LinkBidi(_spBackIdx, FocusNavigator.NavDir.Right, _spTabClassicIdx);
        nav.LinkBidi(_spTabClassicIdx, FocusNavigator.NavDir.Right, _spTabEndlessIdx);
        nav.LinkBidi(_spTabEndlessIdx, FocusNavigator.NavDir.Right, _spLeaderboardIdx);

        // Top-row ↓ targets:
        //   Active tab → currently-selected preset (so pressing ↓ on the
        //   tab jumps to whatever the user picked last, not always the
        //   leftmost). Inactive tab + corner icons fall back to the
        //   nearest column (Back/Classic → topLeft, Endless/Leaderboard
        //   → topRight) since the user shouldn't be lingering there anyway.
        int activeTabIdx = _endlessTabActive ? _spTabEndlessIdx : _spTabClassicIdx;
        int inactiveTabIdx = _endlessTabActive ? _spTabClassicIdx : _spTabEndlessIdx;
        int selectedPresetIdx = GetPresetIndex();

        nav.Link(_spBackIdx, FocusNavigator.NavDir.Down, topLeft);
        nav.Link(_spLeaderboardIdx, FocusNavigator.NavDir.Down, topRight);
        nav.Link(activeTabIdx, FocusNavigator.NavDir.Down, selectedPresetIdx);
        nav.Link(
            inactiveTabIdx,
            FocusNavigator.NavDir.Down,
            inactiveTabIdx == _spTabClassicIdx ? topLeft : topRight
        );

        // Top preset row ↑ → active tab (preserves the "preset belongs to
        // a tab" mental model regardless of which preset column you're on).
        for (int i = 0; i < topRow.Count; i++)
            nav.Link(topRow[i], FocusNavigator.NavDir.Up, activeTabIdx);
    }

    private int GetPresetIndex()
    {
        int b = _spPresetBase;
        if (_endlessTabActive)
        {
            // Endless: b+0 small (5), b+1 medium (10), b+2 large (20).
            if (_endlessSelectedSize == 5)
                return b;
            if (_endlessSelectedSize == 10)
                return b + 1;
            if (_endlessSelectedSize == 20)
                return b + 2;
            return b + 1;
        }
        if (_isCustomSelected)
            return b + 4;
        if (_selectedWidth == 10 && _selectedHeight == 10)
            return b;
        if (_selectedWidth == 20 && _selectedHeight == 20)
            return b + 1;
        if (_selectedWidth == 40 && _selectedHeight == 40)
            return b + 2;
        if (_selectedWidth == 100 && _selectedHeight == 100)
            return b + 3;
        return b;
    }

    private void SelectPreset(int width, int height)
    {
        bool wasCustom = _isCustomSelected;
        _isCustomSelected = false;
        _selectedWidth = width;
        _selectedHeight = height;
        SetVisible(_customPanel, false);
        UpdateAllPresetHighlights();
        if (wasCustom && _sizeSelectInitialized)
            Owner.RebuildNavigator(preserveFocus: true);
        else
            UpdateActiveTabDownLink();
    }

    private void SelectCustom()
    {
        bool wasCustom = _isCustomSelected;
        _isCustomSelected = true;
        _selectedWidth = Mathf.RoundToInt(_customWidthSnap.Value);
        _selectedHeight = Mathf.RoundToInt(_customHeightSnap.Value);
        SetVisible(_customPanel, true);
        UpdateAllPresetHighlights();
        if (!wasCustom && _sizeSelectInitialized)
            Owner.RebuildNavigator(preserveFocus: true);
        else
            UpdateActiveTabDownLink();
    }

    /// <summary>
    /// Re-points the active tab's ↓ link at the currently-selected preset
    /// so pressing Down on the tab jumps to the user's last pick (instead
    /// of always landing on the leftmost preset).
    /// </summary>
    private void UpdateActiveTabDownLink()
    {
        if (
            Owner.CurrentState != MainMenuController.MenuState.Singleplayer
            || !_sizeSelectInitialized
        )
            return;
        int activeTabIdx = _endlessTabActive ? _spTabEndlessIdx : _spTabClassicIdx;
        Owner.Navigator.Link(activeTabIdx, FocusNavigator.NavDir.Down, GetPresetIndex());
    }

    private void UpdateAllPresetHighlights()
    {
        UpdatePresetHighlight(_presetSmall, 10, 10);
        UpdatePresetHighlight(_presetMedium, 20, 20);
        UpdatePresetHighlight(_presetLarge, 40, 40);
        UpdatePresetHighlight(_presetXLarge, 100, 100);
        if (_isCustomSelected)
            _presetCustom.AddToClassList("preset-btn--selected");
        else
            _presetCustom.RemoveFromClassList("preset-btn--selected");
    }

    private void UpdatePresetHighlight(Button btn, int w, int h)
    {
        if (!_isCustomSelected && w == _selectedWidth && h == _selectedHeight)
            btn.AddToClassList("preset-btn--selected");
        else
            btn.RemoveFromClassList("preset-btn--selected");
    }

    // -- Actions ----------------------------------------------------------------

    private static void OnContinue()
    {
        GameSettings.ResumeFromSave();
        SceneNav.Push("Game");
    }

    private static void OnLeaderboard() => SceneNav.Push("Leaderboard");

    private void OnStartGame()
    {
        GameSettings.Apply(_selectedWidth, _selectedHeight);
        GameSettings.Mode = GameMode.Classic;
        SceneNav.Push("Game");
    }

    /// <summary>
    /// Endless mode entry. Square board sized by the endless tab's preset
    /// pick (10 / 20 / 40 — no custom; non-standard sizes wouldn't be
    /// leaderboard-comparable). EndlessMode reads dimensions from
    /// <see cref="GameSettings"/> like classic does.
    /// </summary>
    private void OnStartEndless()
    {
        GameSettings.Apply(_endlessSelectedSize, _endlessSelectedSize);
        GameSettings.Mode = GameMode.Endless;
        SceneNav.Push("Game");
    }

    private static void SetVisible(VisualElement el, bool visible)
    {
        if (visible)
            el.RemoveFromClassList("screen--hidden");
        else
            el.AddToClassList("screen--hidden");
    }
}
