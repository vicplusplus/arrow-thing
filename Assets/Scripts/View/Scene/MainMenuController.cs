using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the main menu UI with nested sub-menus:
///   Root         — Play, Settings, Quit, links
///   Play         — Singleplayer, Multiplayer (side by side)
///   Singleplayer — Size presets, custom sliders, Start, Continue, Leaderboard
///   Multiplayer  — Co-op (coming soon stub)
/// </summary>
public sealed class MainMenuController : NavigableScene
{
    private const string GitHubUrl = "https://github.com/vicplusplus/arrow-thing";
    private const string DiscordUrl = "https://discord.gg/FBwTyaWzpE";

    private enum MenuState
    {
        Root,
        Play,
        Singleplayer,
        Multiplayer,
    }

    // Persists across scene reloads so returning from Game / Leaderboard
    // lands the player back in the sub-menu they came from.
    private static MenuState _persistedState = MenuState.Root;

    private MenuState _currentState = MenuState.Root;
    private VisualElement _menuRoot;
    private VisualElement _menuPlay;
    private VisualElement _menuSingleplayer;
    private VisualElement _menuMultiplayer;
    private ConfirmModal _quitModal;

    private NavGraph _rootNavGraph;
    private NavGraph _playNavGraph;
    private NavGraph _multiplayerNavGraph;

    // -- Size select state -------------------------------------------------------

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

    // Nav graph indices for singleplayer (set in BuildSingleplayerNavGraph)
    private int _spStartIdx;
    private int _spPresetBase;
    private int _spBackIdx;
    private int _spLeaderboardIdx;
    private int _spTabClassicIdx;
    private int _spTabEndlessIdx;

    protected override KeybindManager.Context NavContext => KeybindManager.Context.MainMenu;

    protected override void OnEnable()
    {
        // Deep-link resolution must happen before base.OnEnable calls BuildUI/SetState.
        CoopDeepLink.TryResolve();
        base.OnEnable();

        // If a lobby code was provided via deep-link, jump straight to the hub.
        if (!string.IsNullOrEmpty(GameSettings.PendingLobbyCode))
            SceneNav.Push("CoopHub");
    }

    protected override void BuildUI(VisualElement root)
    {
        _menuRoot = root.Q("menu-root");
        _menuPlay = root.Q("menu-play");
        _menuSingleplayer = root.Q("menu-singleplayer");
        _menuMultiplayer = root.Q("menu-multiplayer");

        // Root buttons
        root.Q<Button>("play-btn").clicked += () => SetState(MenuState.Play);
        root.Q<Button>("settings-btn").clicked += () => SettingsController.Instance.Open();
        root.Q<Button>("link-github-btn").clicked += () => ExternalLinks.Open(GitHubUrl);
        root.Q<Button>("link-discord-btn").clicked += () => ExternalLinks.Open(DiscordUrl);

        var quitBtn = root.Q<Button>("quit-btn");
        if (Application.isMobilePlatform || Application.platform == RuntimePlatform.WebGLPlayer)
            quitBtn.style.display = DisplayStyle.None;
        else
            quitBtn.clicked += OnQuitPressed;

        _quitModal = new ConfirmModal(root.Q("quit-modal"), "Quit game?", "Yes", "No");
        _quitModal.Confirmed += OnQuitConfirm;
        _quitModal.Cancelled += () => _quitModal.Hide();

        // Play buttons
        root.Q<Button>("singleplayer-btn").clicked += () => SetState(MenuState.Singleplayer);
        root.Q<Button>("multiplayer-btn").clicked += () => SetState(MenuState.Multiplayer);
        root.Q<Button>("back-play-btn").clicked += () => SetState(MenuState.Root);

        // Singleplayer buttons (size select + continue + leaderboard)
        InitSizeSelect(root);
        root.Q<Button>("leaderboard-btn").clicked += OnLeaderboard;
        root.Q<Button>("back-sp-btn").clicked += () => SetState(MenuState.Play);

        var continueBtn = root.Q<Button>("continue-btn");
        if (SaveManager.HasSave())
            SetVisible(continueBtn, true);
        continueBtn.clicked += OnContinue;

        // Multiplayer buttons
        root.Q<Button>("coop-btn").clicked += () => SceneNav.Push("CoopHub");
        root.Q<Button>("back-mp-btn").clicked += () => SetState(MenuState.Play);

        // Restore state (e.g. returning from Game via SceneNav.Pop)
        SetState(_persistedState);
    }

    protected override void BuildNavGraph(FocusNavigator nav)
    {
        switch (_currentState)
        {
            case MenuState.Root:
                BuildRootNavGraph(nav);
                break;
            case MenuState.Play:
                BuildPlayNavGraph(nav);
                break;
            case MenuState.Singleplayer:
                BuildSingleplayerNavGraph(nav);
                break;
            case MenuState.Multiplayer:
                BuildMultiplayerNavGraph(nav);
                break;
        }
    }

    protected override void OnUpdate(KeybindManager km)
    {
        if (
            _currentState == MenuState.Singleplayer
            && km.OpenLeaderboard != null
            && km.OpenLeaderboard.WasPerformedThisFrame()
        )
        {
            OnLeaderboard();
        }
    }

    protected override void OnCancel()
    {
        switch (_currentState)
        {
            case MenuState.Singleplayer:
            case MenuState.Multiplayer:
                SetState(MenuState.Play);
                break;
            case MenuState.Play:
                SetState(MenuState.Root);
                break;
            case MenuState.Root:
                if (
                    !Application.isMobilePlatform
                    && Application.platform != RuntimePlatform.WebGLPlayer
                )
                    OnQuitPressed();
                break;
        }
    }

    // -- State management -------------------------------------------------------

    private void SetState(MenuState state)
    {
        _currentState = state;
        _persistedState = state;
        SetVisible(_menuRoot, state == MenuState.Root);
        SetVisible(_menuPlay, state == MenuState.Play);
        SetVisible(_menuSingleplayer, state == MenuState.Singleplayer);
        SetVisible(_menuMultiplayer, state == MenuState.Multiplayer);
        RebuildNavigator();
    }

    // -- Nav graphs per state ---------------------------------------------------

    private void BuildRootNavGraph(FocusNavigator nav)
    {
        if (_rootNavGraph == null)
            _rootNavGraph = Resources.Load<NavGraph>("NavGraphs/MainMenuRoot");

        var quitBtn = Root.Q<Button>("quit-btn");
        bool hasQuit =
            quitBtn != null
            && !Application.isMobilePlatform
            && Application.platform != RuntimePlatform.WebGLPlayer;

        new NavGraphBuilder(_rootNavGraph)
            .Bind(
                "Quit",
                hasQuit ? quitBtn : null,
                onActivate: () =>
                {
                    OnQuitPressed();
                    return true;
                }
            )
            .Bind(
                "Play",
                Root.Q<Button>("play-btn"),
                onActivate: () =>
                {
                    SetState(MenuState.Play);
                    return true;
                }
            )
            .Bind(
                "Settings",
                Root.Q<Button>("settings-btn"),
                onActivate: () =>
                {
                    SettingsController.Instance.Open();
                    return true;
                }
            )
            .Bind(
                "GitHub",
                Root.Q<Button>("link-github-btn"),
                onActivate: () =>
                {
                    ExternalLinks.Open(GitHubUrl);
                    return true;
                }
            )
            .Bind(
                "Discord",
                Root.Q<Button>("link-discord-btn"),
                onActivate: () =>
                {
                    ExternalLinks.Open(DiscordUrl);
                    return true;
                }
            )
            .Apply(nav);
    }

    private void BuildPlayNavGraph(FocusNavigator nav)
    {
        if (_playNavGraph == null)
            _playNavGraph = Resources.Load<NavGraph>("NavGraphs/MainMenuPlay");

        new NavGraphBuilder(_playNavGraph)
            .Bind(
                "Back",
                Root.Q<Button>("back-play-btn"),
                onActivate: () =>
                {
                    SetState(MenuState.Root);
                    return true;
                }
            )
            .Bind(
                "Singleplayer",
                Root.Q<Button>("singleplayer-btn"),
                onActivate: () =>
                {
                    SetState(MenuState.Singleplayer);
                    return true;
                }
            )
            .Bind(
                "Multiplayer",
                Root.Q<Button>("multiplayer-btn"),
                onActivate: () =>
                {
                    SetState(MenuState.Multiplayer);
                    return true;
                }
            )
            .Apply(nav);
    }

    private void BuildMultiplayerNavGraph(FocusNavigator nav)
    {
        if (_multiplayerNavGraph == null)
            _multiplayerNavGraph = Resources.Load<NavGraph>("NavGraphs/MainMenuMultiplayer");

        new NavGraphBuilder(_multiplayerNavGraph)
            .Bind(
                "Back",
                Root.Q<Button>("back-mp-btn"),
                onActivate: () =>
                {
                    SetState(MenuState.Play);
                    return true;
                }
            )
            .Bind(
                "Coop",
                Root.Q<Button>("coop-btn"),
                onActivate: () =>
                {
                    SceneNav.Push("CoopHub");
                    return true;
                }
            )
            .Apply(nav);
    }

    // -- Singleplayer: size select + continue + leaderboard ---------------------

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

    // -- Singleplayer endless tab -----------------------------------------------

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
            RebuildNavigator(preserveFocus: true);
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

    private void BuildSingleplayerNavGraph(FocusNavigator nav)
    {
        var items = new List<FocusNavigator.FocusItem>();

        _spBackIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("back-sp-btn"),
                OnActivate = () =>
                {
                    SetState(MenuState.Play);
                    return true;
                },
            }
        );

        _spLeaderboardIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("leaderboard-btn"),
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
                    Element = Root.Q<Button>("endless-btn"),
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
                    Element = Root.Q<Button>("start-btn"),
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
        var continueBtn = Root.Q<Button>("continue-btn");
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
        if (_currentState != MenuState.Singleplayer)
            return;

        Navigator.ClearLinks();

        // Re-link tab pair (ClearLinks wipes everything, including the bidi
        // we set in BuildSingleplayerNavGraph).
        Navigator.LinkBidi(_spTabClassicIdx, FocusNavigator.NavDir.Right, _spTabEndlessIdx);

        // Re-link Start/Continue side by side (classic only).
        var continueBtn = Root.Q<Button>("continue-btn");
        bool hasContinue = !_endlessTabActive && !continueBtn.ClassListContains("screen--hidden");
        if (hasContinue)
        {
            int continueIdx = _spStartIdx + 1;
            Navigator.LinkBidi(_spStartIdx, FocusNavigator.NavDir.Right, continueIdx);
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
                Navigator.LinkBidi(row[i], FocusNavigator.NavDir.Right, row[i + 1]);
            if (r < rows.Count - 1)
            {
                int last = row[row.Count - 1];
                int nextFirst = rows[r + 1][0];
                Navigator.Link(last, FocusNavigator.NavDir.Right, nextFirst);
                Navigator.Link(nextFirst, FocusNavigator.NavDir.Left, last);
            }
        }

        for (int r = 0; r < rows.Count - 1; r++)
        {
            var upper = rows[r];
            var lower = rows[r + 1];
            for (int i = 0; i < upper.Count; i++)
            {
                int target = i < lower.Count ? lower[i] : lower[lower.Count - 1];
                Navigator.LinkBidi(upper[i], FocusNavigator.NavDir.Down, target);
            }
            for (int i = upper.Count; i < lower.Count; i++)
                Navigator.Link(lower[i], FocusNavigator.NavDir.Up, upper[upper.Count - 1]);
        }

        var lastPresetRow = rows[rows.Count - 1];

        if (_endlessTabActive)
        {
            // Endless: every preset → endless start. Start ↑ goes to the
            // closest preset (tracked column).
            foreach (int idx in lastPresetRow)
                Navigator.LinkBidi(idx, FocusNavigator.NavDir.Down, _spStartIdx);
            Navigator.Link(
                _spStartIdx,
                FocusNavigator.NavDir.Up,
                lastPresetRow[lastPresetRow.Count - 1]
            );
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
                    Navigator.LinkBidi(idx, FocusNavigator.NavDir.Down, firstSlider);
                // Custom slider chain → Start
                Navigator.LinkChain(customIdx, _spStartIdx - customIdx + 1);
                // Continue ↑ → last slider (height)
                if (hasContinue)
                {
                    int heightSliderIdx = _spStartIdx - 1;
                    Navigator.Link(_spStartIdx + 1, FocusNavigator.NavDir.Up, heightSliderIdx);
                }
            }
            else
            {
                // Non-Custom presets in last row → Start
                foreach (int idx in lastPresetRow)
                {
                    if (idx != customIdx)
                        Navigator.LinkBidi(idx, FocusNavigator.NavDir.Down, _spStartIdx);
                }
                // Custom → Continue (if exists), else Start
                int customTarget = hasContinue ? _spStartIdx + 1 : _spStartIdx;
                Navigator.LinkBidi(customIdx, FocusNavigator.NavDir.Down, customTarget);
                // Start ↑ → XLarge, Continue ↑ → Custom
                Navigator.Link(_spStartIdx, FocusNavigator.NavDir.Up, xlargeIdx);
                if (hasContinue)
                    Navigator.Link(_spStartIdx + 1, FocusNavigator.NavDir.Up, customIdx);
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
        Navigator.LinkBidi(_spBackIdx, FocusNavigator.NavDir.Right, _spTabClassicIdx);
        Navigator.LinkBidi(_spTabClassicIdx, FocusNavigator.NavDir.Right, _spTabEndlessIdx);
        Navigator.LinkBidi(_spTabEndlessIdx, FocusNavigator.NavDir.Right, _spLeaderboardIdx);

        // Top-row ↓ targets:
        //   Active tab → currently-selected preset (so pressing ↓ on the
        //   tab jumps to whatever the user picked last, not always the
        //   leftmost). Inactive tab + corner icons fall back to the
        //   nearest column (Back/Classic → topLeft, Endless/Leaderboard
        //   → topRight) since the user shouldn't be lingering there anyway.
        int activeTabIdx = _endlessTabActive ? _spTabEndlessIdx : _spTabClassicIdx;
        int inactiveTabIdx = _endlessTabActive ? _spTabClassicIdx : _spTabEndlessIdx;
        int selectedPresetIdx = GetPresetIndex();

        Navigator.Link(_spBackIdx, FocusNavigator.NavDir.Down, topLeft);
        Navigator.Link(_spLeaderboardIdx, FocusNavigator.NavDir.Down, topRight);
        Navigator.Link(activeTabIdx, FocusNavigator.NavDir.Down, selectedPresetIdx);
        Navigator.Link(
            inactiveTabIdx,
            FocusNavigator.NavDir.Down,
            inactiveTabIdx == _spTabClassicIdx ? topLeft : topRight
        );

        // Top preset row ↑ → active tab (preserves the "preset belongs to
        // a tab" mental model regardless of which preset column you're on).
        for (int i = 0; i < topRow.Count; i++)
            Navigator.Link(topRow[i], FocusNavigator.NavDir.Up, activeTabIdx);
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
            RebuildNavigator(preserveFocus: true);
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
            RebuildNavigator(preserveFocus: true);
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
        if (_currentState != MenuState.Singleplayer || !_sizeSelectInitialized)
            return;
        int activeTabIdx = _endlessTabActive ? _spTabEndlessIdx : _spTabClassicIdx;
        Navigator.Link(activeTabIdx, FocusNavigator.NavDir.Down, GetPresetIndex());
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

    // -- Actions -----------------------------------------------------------------

    private void OnContinue()
    {
        GameSettings.ResumeFromSave();
        SceneNav.Push("Game");
    }

    private void OnLeaderboard() => SceneNav.Push("Leaderboard");

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

    private void OnQuitPressed() => _quitModal.Show();

    private void OnQuitConfirm()
    {
        Application.Quit();
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#endif
    }

    private static void SetVisible(VisualElement el, bool visible)
    {
        if (visible)
            el.RemoveFromClassList("screen--hidden");
        else
            el.AddToClassList("screen--hidden");
    }
}
