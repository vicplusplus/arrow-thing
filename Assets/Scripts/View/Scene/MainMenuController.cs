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

    // Nav graph indices for singleplayer (set in BuildSingleplayerNavGraph)
    private int _spStartIdx;
    private int _spPresetBase;
    private int _spBackIdx;
    private int _spLeaderboardIdx;

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
        var items = new List<FocusNavigator.FocusItem>();

        var quitBtn = Root.Q<Button>("quit-btn");
        bool hasQuit =
            quitBtn != null
            && !Application.isMobilePlatform
            && Application.platform != RuntimePlatform.WebGLPlayer;
        int quitIdx = -1;
        if (hasQuit)
        {
            quitIdx = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = quitBtn,
                    OnActivate = () =>
                    {
                        OnQuitPressed();
                        return true;
                    },
                }
            );
        }

        int playIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("play-btn"),
                OnActivate = () =>
                {
                    SetState(MenuState.Play);
                    return true;
                },
            }
        );

        int settingsIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("settings-btn"),
                OnActivate = () =>
                {
                    SettingsController.Instance.Open();
                    return true;
                },
            }
        );

        int githubIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("link-github-btn"),
                OnActivate = () =>
                {
                    ExternalLinks.Open(GitHubUrl);
                    return true;
                },
            }
        );

        int discordIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("link-discord-btn"),
                OnActivate = () =>
                {
                    ExternalLinks.Open(DiscordUrl);
                    return true;
                },
            }
        );

        nav.SetItems(items, playIdx);

        if (hasQuit)
        {
            nav.Link(quitIdx, FocusNavigator.NavDir.Down, playIdx);
            nav.Link(playIdx, FocusNavigator.NavDir.Up, quitIdx);
            nav.Link(playIdx, FocusNavigator.NavDir.Left, quitIdx);
        }

        nav.LinkBidi(playIdx, FocusNavigator.NavDir.Down, settingsIdx);

        nav.LinkBidi(settingsIdx, FocusNavigator.NavDir.Down, discordIdx);
        nav.Link(settingsIdx, FocusNavigator.NavDir.Left, discordIdx);

        nav.LinkBidi(githubIdx, FocusNavigator.NavDir.Right, discordIdx);
        nav.Link(githubIdx, FocusNavigator.NavDir.Up, settingsIdx);
    }

    private void BuildPlayNavGraph(FocusNavigator nav)
    {
        var items = new List<FocusNavigator.FocusItem>();

        int backIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("back-play-btn"),
                OnActivate = () =>
                {
                    SetState(MenuState.Root);
                    return true;
                },
            }
        );

        int spIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("singleplayer-btn"),
                OnActivate = () =>
                {
                    SetState(MenuState.Singleplayer);
                    return true;
                },
            }
        );

        int mpIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("multiplayer-btn"),
                OnActivate = () =>
                {
                    SetState(MenuState.Multiplayer);
                    return true;
                },
            }
        );

        nav.SetItems(items, spIdx);

        nav.Link(backIdx, FocusNavigator.NavDir.Down, spIdx);
        nav.Link(spIdx, FocusNavigator.NavDir.Up, backIdx);
        nav.Link(spIdx, FocusNavigator.NavDir.Left, backIdx);
        nav.Link(mpIdx, FocusNavigator.NavDir.Up, backIdx);
        nav.LinkBidi(spIdx, FocusNavigator.NavDir.Right, mpIdx);
    }

    private void BuildMultiplayerNavGraph(FocusNavigator nav)
    {
        var items = new List<FocusNavigator.FocusItem>();

        int backIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("back-mp-btn"),
                OnActivate = () =>
                {
                    SetState(MenuState.Play);
                    return true;
                },
            }
        );

        int coopIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("coop-btn"),
                OnActivate = () =>
                {
                    SceneNav.Push("CoopHub");
                    return true;
                },
            }
        );

        nav.SetItems(items, coopIdx);

        nav.Link(backIdx, FocusNavigator.NavDir.Down, coopIdx);
        nav.Link(coopIdx, FocusNavigator.NavDir.Up, backIdx);
        nav.Link(coopIdx, FocusNavigator.NavDir.Left, backIdx);
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
        var endlessBtn = root.Q<Button>("endless-btn");
        if (endlessBtn != null)
            endlessBtn.clicked += OnStartEndless;

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

        _spPresetBase = items.Count;
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

        int startIdx = items.Count;
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
        _spStartIdx = startIdx;

        var continueBtn = Root.Q<Button>("continue-btn");
        bool hasContinue = !continueBtn.ClassListContains("screen--hidden");
        int continueIdx = -1;
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

        // Grid links built after layout resolves.
        var presetGrid = Root.Q(className: "preset-grid");
        if (presetGrid != null)
        {
            presetGrid.UnregisterCallback<GeometryChangedEvent>(OnPresetGridLayout);
            presetGrid.RegisterCallback<GeometryChangedEvent>(OnPresetGridLayout);
        }

        // Start / Continue side by side.
        if (hasContinue)
            nav.LinkBidi(startIdx, FocusNavigator.NavDir.Right, continueIdx);
    }

    private void OnPresetGridLayout(GeometryChangedEvent evt) => LinkPresetGrid();

    private void LinkPresetGrid()
    {
        if (_currentState != MenuState.Singleplayer)
            return;

        Navigator.ClearLinks();

        // Re-link Start/Continue side by side (ClearLinks wipes everything).
        var continueBtn = Root.Q<Button>("continue-btn");
        bool hasContinue = !continueBtn.ClassListContains("screen--hidden");
        if (hasContinue)
        {
            int continueIdx = _spStartIdx + 1;
            Navigator.LinkBidi(_spStartIdx, FocusNavigator.NavDir.Right, continueIdx);
        }

        int b = _spPresetBase;
        Button[] presets =
        {
            _presetSmall,
            _presetMedium,
            _presetLarge,
            _presetXLarge,
            _presetCustom,
        };

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

        var topRow = rows[0];
        int topLeft = topRow[0];
        int topRight = topRow[topRow.Count - 1];

        Navigator.Link(_spBackIdx, FocusNavigator.NavDir.Down, topLeft);
        Navigator.Link(_spBackIdx, FocusNavigator.NavDir.Right, topLeft);
        Navigator.LinkBidi(_spBackIdx, FocusNavigator.NavDir.Right, _spLeaderboardIdx);
        Navigator.Link(_spLeaderboardIdx, FocusNavigator.NavDir.Down, topRight);
        Navigator.Link(topLeft, FocusNavigator.NavDir.Up, _spBackIdx);
        Navigator.Link(topLeft, FocusNavigator.NavDir.Left, _spBackIdx);
        for (int i = 1; i < topRow.Count; i++)
            Navigator.Link(topRow[i], FocusNavigator.NavDir.Up, _spLeaderboardIdx);
    }

    private int GetPresetIndex()
    {
        int b = _spPresetBase;
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
    /// Endless mode entry: same size selection but mode flag flipped so
    /// <see cref="GameController.CreateMode"/> instantiates <see cref="EndlessMode"/>.
    /// </summary>
    private void OnStartEndless()
    {
        GameSettings.Apply(_selectedWidth, _selectedHeight);
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
