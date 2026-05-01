using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

/// <summary>
/// Singleton settings panel that persists across all scenes. Bootstrapped automatically
/// from Assets/Resources/SettingsController.prefab before the first scene loads.
/// Call SettingsController.Instance.Open() / .Close() / .Toggle() from any scene.
/// The prefab must have a UIDocument (pointing to SettingsDocument.uxml) with a
/// PanelSettings sort order higher than the game UI, a UIThemeApplier, and this
/// component.
/// </summary>
public sealed class SettingsController : MonoBehaviour
{
    public static SettingsController Instance { get; private set; }

    /// <summary>Fired with <c>true</c> when settings opens, <c>false</c> when it closes.</summary>
    public static event Action<bool> IsOpenChanged;

    /// <summary>True for one frame after settings was closed via Escape.
    /// Other controllers should skip their own Cancel handling.</summary>
    public static bool JustClosed { get; private set; }

    private VisualElement _settings;
    private VisualElement _settingsPanel;
    private VisualElement _settingsBackdrop;
    private AccountManager _accountManager;
    private CustomDropdown _themeDropdown;
    private ConfirmModal _clearScoresModal;
    private ConfirmModal _externalLinkModal;
    private Label _externalLinkLabel;
    private bool _navScrollPending;
    private SnapSlider _dragSnap;
    private SnapSlider _zoomSnap;
    private KeybindSettingsSection _keybindSection;

    // Keyboard navigation.
    private FocusNavigator _navigator;
    private int _lastFocusedIndex;
    private KeybindManager.Context _previousContext;
    private FocusNavigator _previousActiveNavigator;

    public bool IsOpen { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null)
            return;

        var prefab = Resources.Load<GameObject>("SettingsController");
        if (prefab == null)
        {
            Debug.LogError(
                "[SettingsController] Prefab not found at Resources/SettingsController. "
                    + "Create a prefab there with UIDocument, UIThemeApplier, and SettingsController."
            );
            return;
        }
        var go = Instantiate(prefab);
        go.name = "SettingsController";
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        Instance = this;
    }

    private void OnEnable()
    {
        ExternalLinks.LinkRequested += OnExternalLinkRequested;
    }

    private void OnDisable()
    {
        ExternalLinks.LinkRequested -= OnExternalLinkRequested;
    }

    private void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        _settings = root.Q("settings");

        if (_settings == null)
        {
            Debug.LogError(
                "[SettingsController] Could not find 'settings' element in UIDocument root. "
                    + "Ensure the UIDocument's Source Asset on the SettingsController prefab "
                    + "is set to SettingsDocument.uxml."
            );
            return;
        }

        var settingsScreen = _settings.Q(className: "settings-screen");
        _settingsPanel = settingsScreen?.Q(className: "settings-panel");
        _settingsBackdrop = settingsScreen?.Q("settings-backdrop");
        if (settingsScreen != null)
            settingsScreen.RegisterCallback<GeometryChangedEvent>(OnSettingsGeometryChanged);

        // Suppress Unity's built-in keyboard navigation globally. This UIDocument
        // persists across all scenes (DontDestroyOnLoad), so it covers the gap
        // between scene load and each scene's FocusNavigator creation.
        root.RegisterCallback<NavigationMoveEvent>(
            e => e.StopPropagation(),
            TrickleDown.TrickleDown
        );
        root.RegisterCallback<NavigationSubmitEvent>(
            e => e.StopPropagation(),
            TrickleDown.TrickleDown
        );
        root.RegisterCallback<NavigationCancelEvent>(
            e => e.StopPropagation(),
            TrickleDown.TrickleDown
        );

        WireSettingsControls();
        WireSettingsNav();
        WireModals(root);

        _accountManager = new AccountManager(_settings, root.Q("logout-modal"));
        _accountManager.FormChanged += focusTarget =>
        {
            if (!IsOpen)
                return;
            BuildNavigator();
            if (focusTarget != null && _navigator != null)
            {
                // Find the item index matching the focus target and focus it.
                for (int i = 0; i < _navigator.ItemCount; i++)
                {
                    if (_navigator.GetItemElement(i) == focusTarget)
                    {
                        _navigator.SetFocus(i);
                        break;
                    }
                }
            }
        };

        // ToggleSettings keybind is owned by KeybindManager (always active).
        if (KeybindManager.Instance != null)
        {
            KeybindManager.Instance.ToggleSettings.performed += _ =>
            {
                if (KeybindManager.Instance.TextFieldFocused)
                    return;
                Toggle();
            };
        }
    }

    public void Open()
    {
        if (IsOpen)
            return;
        IsOpen = true;
        SetVisible(_settings, true);
        IsOpenChanged?.Invoke(true);

        // Save previous context and active navigator.
        if (KeybindManager.Instance != null)
        {
            _previousContext = KeybindManager.Instance.ActiveContext;
            KeybindManager.Instance.ActiveContext = KeybindManager.Context.Settings;
        }
        _previousActiveNavigator = FocusNavigator.Active;

        BuildNavigator();
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        if (_themeDropdown != null)
            _themeDropdown.Close();
        _accountManager.CancelEditing();
        SetVisible(_settings, false);
        IsOpenChanged?.Invoke(false);

        // Save focus position and restore previous context + navigator.
        if (_navigator != null)
            _lastFocusedIndex = _navigator.CurrentIndex;
        if (KeybindManager.Instance != null)
            KeybindManager.Instance.ActiveContext = _previousContext;
        if (_previousActiveNavigator != null)
        {
            FocusNavigator.Active = _previousActiveNavigator;
            _previousActiveNavigator = null;
        }
    }

    private void Update()
    {
        JustClosed = false;

        if (!IsOpen || _navigator == null)
            return;

        var km = KeybindManager.Instance;
        if (km == null)
            return;

        // Dropdown open: it handles its own navigation.
        if (_themeDropdown != null && _themeDropdown.IsOpen)
        {
            _themeDropdown.UpdateKeyboard();
            // If the dropdown just closed, suppress DAS so held keys don't
            // immediately fire as new presses in the FocusNavigator.
            if (!_themeDropdown.IsOpen && _navigator != null)
                _navigator.SuppressDAS();
            return;
        }

        _navigator.Update();

        if (km.Cancel.WasPerformedThisFrame() && !_navigator.ConsumesCancel && !km.TextFieldFocused)
        {
            Close();
            JustClosed = true;
        }
    }

    public void Toggle()
    {
        if (IsOpen)
            Close();
        else
            Open();
    }

    private void BuildNavigator()
    {
        _navigator?.Dispose();
        _navigator = new FocusNavigator(_settings);

        var items = new List<FocusNavigator.FocusItem>();
        var settingsScroll = _settings.Q<ScrollView>("settings-scroll");

        // ── Tabs (left column) ──────────────────────────────────────────
        string[] tabNames =
        {
            "nav-account",
            "nav-gameplay",
            "nav-keybinds",
            "nav-data",
            "nav-about",
        };
        string[] sectionNames =
        {
            "section-account",
            "section-gameplay",
            "section-keybinds",
            "section-data",
            "section-about",
        };
        int[] tabIndices = new int[tabNames.Length];

        for (int i = 0; i < tabNames.Length; i++)
        {
            int sectionIdx = i;
            var btn = _settings.Q<Button>(tabNames[i]);
            tabIndices[i] = items.Count;
            items.Add(
                new FocusNavigator.FocusItem
                {
                    Element = btn,
                    OnActivate = () =>
                    {
                        var section = _settings.Q(sectionNames[sectionIdx]);
                        if (section != null && settingsScroll != null)
                        {
                            _navScrollPending = true;
                            ScrollToSection(settingsScroll, section);
                            SetNavActive(
                                _settings
                                    .Query<Button>(className: "settings-nav-btn")
                                    .ToList()
                                    .ToArray(),
                                sectionIdx
                            );
                        }
                        return true;
                    },
                }
            );
        }

        // ── Content (right column) ──────────────────────────────────────
        // Track the first content item index per section for tab→content linking.
        int[] sectionFirstContent = new int[tabNames.Length];
        for (int i = 0; i < sectionFirstContent.Length; i++)
            sectionFirstContent[i] = -1;

        // Helper: add a content item and record it as the first for a section.
        void AddContent(int sectionIdx, FocusNavigator.FocusItem item)
        {
            if (sectionFirstContent[sectionIdx] < 0)
                sectionFirstContent[sectionIdx] = items.Count;
            items.Add(item);
        }

        // — Account section (0) — delegated to AccountManager.
        int accountStart = items.Count;
        foreach (var item in _accountManager.GetFocusItems())
            AddContent(0, item);

        // — Gameplay section (1) —
        if (_themeDropdown != null)
        {
            AddContent(
                1,
                new FocusNavigator.FocusItem
                {
                    Element = _themeDropdown.Root,
                    OnActivate = () =>
                    {
                        _themeDropdown.ActivateFromKeyboard();
                        return true;
                    },
                    OnHorizontal = dir =>
                    {
                        if (dir > 0)
                        {
                            _themeDropdown.ActivateFromKeyboard();
                            return true;
                        }
                        if (dir < 0 && _themeDropdown.IsOpen)
                        {
                            _themeDropdown.Close();
                            return true;
                        }
                        return false; // Let graph edge handle (e.g. Left → tab).
                    },
                }
            );
        }
        if (_dragSnap != null)
        {
            AddContent(
                1,
                new FocusNavigator.FocusItem
                {
                    Element = _dragSnap.Track,
                    CustomFocusVisual = true,
                    OnHorizontal = dir =>
                    {
                        bool shift =
                            UnityEngine.InputSystem.Keyboard.current != null
                            && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;
                        _dragSnap.KeyboardStep(dir, shift);
                        return true;
                    },
                }
            );
        }
        if (_zoomSnap != null)
        {
            AddContent(
                1,
                new FocusNavigator.FocusItem
                {
                    Element = _zoomSnap.Track,
                    CustomFocusVisual = true,
                    OnHorizontal = dir =>
                    {
                        bool shift =
                            UnityEngine.InputSystem.Keyboard.current != null
                            && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;
                        _zoomSnap.KeyboardStep(dir, shift);
                        return true;
                    },
                }
            );
        }
        var keepTrailToggle = _settings.Q<Toggle>("keep-trail-toggle");
        if (keepTrailToggle != null)
        {
            AddContent(
                1,
                new FocusNavigator.FocusItem
                {
                    Element = keepTrailToggle,
                    OnActivate = () =>
                    {
                        keepTrailToggle.value = !keepTrailToggle.value;
                        return true;
                    },
                    OnHorizontal = dir =>
                    {
                        keepTrailToggle.value = dir > 0;
                        return true;
                    },
                }
            );
        }

        // — Keybinds section (2) —
        int keybindStart = -1;
        if (_keybindSection != null)
        {
            keybindStart = items.Count;
            foreach (var item in _keybindSection.GetFocusItems())
                AddContent(2, item);
        }

        // — Data section (3) —
        var clearBtn = _settings.Q<Button>("clear-scores-btn");
        if (clearBtn != null)
            AddContent(
                3,
                new FocusNavigator.FocusItem
                {
                    Element = clearBtn,
                    OnActivate = () =>
                    {
                        OnClearScores();
                        return true;
                    },
                }
            );

        // — About section (4) —
        var githubBtn = _settings.Q<Button>("about-github-btn");
        if (githubBtn != null)
            AddContent(
                4,
                new FocusNavigator.FocusItem
                {
                    Element = githubBtn,
                    OnActivate = () =>
                    {
                        ExternalLinks.Open(ExternalLinks.GitHub);
                        return true;
                    },
                }
            );
        var discordBtn = _settings.Q<Button>("about-discord-btn");
        if (discordBtn != null)
            AddContent(
                4,
                new FocusNavigator.FocusItem
                {
                    Element = discordBtn,
                    OnActivate = () =>
                    {
                        ExternalLinks.Open(ExternalLinks.Discord);
                        return true;
                    },
                }
            );

        // ── Set items ───────────────────────────────────────────────────
        // Default to first content item (display name), not tabs.
        int firstContent = sectionFirstContent[0] >= 0 ? sectionFirstContent[0] : 0;
        int idx = Mathf.Clamp(_lastFocusedIndex, 0, Mathf.Max(0, items.Count - 1));
        if (idx < tabNames.Length && !FocusNavigator.WasKeyboardActive)
            idx = firstContent;
        _navigator.SetItems(items, idx);

        // ── Build nav graph ─────────────────────────────────────────────
        // Tabs: vertical chain.
        for (int i = 0; i < tabNames.Length - 1; i++)
            _navigator.LinkBidi(tabIndices[i], FocusNavigator.NavDir.Down, tabIndices[i + 1]);

        // Tab → Right → first content item. All content items → Left → their tab.
        for (int i = 0; i < tabNames.Length; i++)
        {
            int contentStart = sectionFirstContent[i];
            if (contentStart < 0)
                continue;

            // Tab → Right → first content item of this section.
            _navigator.Link(tabIndices[i], FocusNavigator.NavDir.Right, contentStart);

            // Find the end of this section's content items.
            int contentEnd = items.Count - 1;
            for (int ns = i + 1; ns < tabNames.Length; ns++)
            {
                if (sectionFirstContent[ns] >= 0)
                {
                    contentEnd = sectionFirstContent[ns] - 1;
                    break;
                }
            }

            // Every content item in this section → Left → corresponding tab.
            for (int ci = contentStart; ci <= contentEnd; ci++)
                _navigator.Link(ci, FocusNavigator.NavDir.Left, tabIndices[i]);
        }

        // Content items: vertical chain within each section, then link sections together.
        int prevLastContent = -1;
        for (int s = 0; s < tabNames.Length; s++)
        {
            int start = sectionFirstContent[s];
            if (start < 0)
                continue;

            // Find end of this section's content items.
            int end = start;
            int nextSectionStart = items.Count;
            for (int ns = s + 1; ns < tabNames.Length; ns++)
            {
                if (sectionFirstContent[ns] >= 0)
                {
                    nextSectionStart = sectionFirstContent[ns];
                    break;
                }
            }
            end = nextSectionStart - 1;

            // Chain within section.
            if (end > start)
                _navigator.LinkChain(start, end - start + 1);

            // About section: social buttons are side by side (same as login/register).
            if (s == 4 && githubBtn != null && discordBtn != null)
            {
                int gi = items.Count - 2; // github
                int di = items.Count - 1; // discord
                _navigator.LinkBidi(gi, FocusNavigator.NavDir.Right, di);

                // Both share the same Up target (clear scores, last item of Data).
                // Remove the chain's vertical link between them.
                int above = sectionFirstContent[3] >= 0 ? sectionFirstContent[3] : -1;
                if (above >= 0)
                {
                    _navigator.Link(gi, FocusNavigator.NavDir.Up, above);
                    _navigator.Link(di, FocusNavigator.NavDir.Up, above);
                    _navigator.Link(above, FocusNavigator.NavDir.Down, gi);
                }
                // Clear the chain's gi→Down→di so Down from GitHub doesn't
                // go to Discord (they're a horizontal row, not vertical).
                _navigator.ClearLink(gi, FocusNavigator.NavDir.Down);
            }

            // Link last content of previous section → first content of this section.
            if (prevLastContent >= 0)
                _navigator.LinkBidi(prevLastContent, FocusNavigator.NavDir.Down, start);

            prevLastContent = end;
        }

        // Account horizontal pairs (e.g. Login ↔ Register).
        // Indices from GetFocusItems are relative — offset by where account items start.
        // For each pair: both items share the same up/down neighbors (the pair acts
        // as a single row in the vertical chain).
        foreach (var (a, b) in _accountManager.HorizontalPairs)
        {
            int absA = accountStart + a;
            int absB = accountStart + b;
            _navigator.LinkBidi(absA, FocusNavigator.NavDir.Right, absB);

            // The chain linked A→Down→B. Override: A and B both go down to
            // whatever was below B, and both go up to whatever was above A.
            // Find the item above A (its current Up target).
            int above = -1;
            int below = -1;
            // In the chain, A's Up and B's Down are the correct neighbors.
            // A was linked: Up→(A-1), Down→B. B was linked: Up→A, Down→(B+1).
            // We want: A.Down = B.Down, B.Up = A.Up, and the neighbors to point
            // to both A and B.
            if (absA > 0)
                above = absA - 1; // item above the pair
            if (absB + 1 < items.Count)
                below = absB + 1; // item below the pair

            // Both pair items ↔ above
            if (above >= 0)
            {
                _navigator.Link(absA, FocusNavigator.NavDir.Up, above);
                _navigator.Link(absB, FocusNavigator.NavDir.Up, above);
                _navigator.Link(above, FocusNavigator.NavDir.Down, absA);
            }

            // Both pair items ↔ below
            if (below >= 0)
            {
                _navigator.Link(absA, FocusNavigator.NavDir.Down, below);
                _navigator.Link(absB, FocusNavigator.NavDir.Down, below);
                _navigator.Link(below, FocusNavigator.NavDir.Up, absA);
            }
        }

        // About social buttons: same horizontal pair pattern.
        if (githubBtn != null && discordBtn != null)
        {
            int gi = -1,
                di = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Element == githubBtn)
                    gi = i;
                if (items[i].Element == discordBtn)
                    di = i;
            }
            if (gi >= 0 && di >= 0)
            {
                // Both point up to the item before github (clear scores).
                int above = gi > 0 ? gi - 1 : -1;
                if (above >= 0)
                {
                    _navigator.Link(gi, FocusNavigator.NavDir.Up, above);
                    _navigator.Link(di, FocusNavigator.NavDir.Up, above);
                    _navigator.Link(above, FocusNavigator.NavDir.Down, gi);
                }
            }
        }

        // Keybind key↔reset pairs as 2×N grid.
        if (_keybindSection != null && keybindStart >= 0)
        {
            // Above: last item of Gameplay section (keep trail toggle).
            int aboveKeybinds = keybindStart > 0 ? keybindStart - 1 : -1;
            // Below: first item of Data section (clear scores).
            int belowKeybinds = sectionFirstContent[3] >= 0 ? sectionFirstContent[3] : -1;
            _keybindSection.LinkNavigation(_navigator, keybindStart, aboveKeybinds, belowKeybinds);
        }
    }

    // -- Responsive layout --------------------------------------------------

    private void OnSettingsGeometryChanged(GeometryChangedEvent evt)
    {
        if (_settingsPanel == null || evt.newRect.height <= 0)
            return;

        float ratio = evt.newRect.width / evt.newRect.height;

        // Breakpoints: >=1.5 wide landscape → 50%; taper to 100% at 3:4; portrait → fullscreen.
        float widthPct;
        float maxWidthPx;
        if (ratio >= 1.5f)
        {
            widthPct = 50f;
            maxWidthPx = 520f;
        }
        else if (ratio >= 1.0f)
        {
            widthPct = Mathf.Lerp(65f, 50f, (ratio - 1.0f) / 0.5f);
            maxWidthPx = 520f;
        }
        else if (ratio >= 0.75f)
        {
            widthPct = Mathf.Lerp(100f, 65f, (ratio - 0.75f) / 0.25f);
            maxWidthPx = 9999f;
        }
        else
        {
            widthPct = 100f;
            maxWidthPx = 9999f;
        }

        _settingsPanel.style.width = new StyleLength(new Length(widthPct, LengthUnit.Percent));
        _settingsPanel.style.maxWidth = new StyleLength(new Length(maxWidthPx, LengthUnit.Pixel));
        if (_settingsBackdrop != null)
            _settingsBackdrop.style.display =
                widthPct >= 100f ? DisplayStyle.None : DisplayStyle.Flex;
    }

    // -- Settings controls --------------------------------------------------

    private void WireSettingsControls()
    {
        float savedThreshold = PlayerPrefs.GetFloat(
            GameSettings.DragThresholdPrefKey,
            GameSettings.DefaultDragThreshold
        );
        _dragSnap = new SnapSlider(
            GameSettings.MinDragThreshold,
            GameSettings.MaxDragThreshold,
            savedThreshold,
            smallStep: 1f,
            snapStep: 0f,
            format: "0",
            showLock: false
        );
        _dragSnap.OnValueChanged += val =>
            PlayerPrefs.SetFloat(GameSettings.DragThresholdPrefKey, val);
        _dragSnap.Root.AddToClassList("setting-snap-slider");
        _settings.Q("drag-threshold-row").Add(_dragSnap.Root);

        float savedZoom = PlayerPrefs.GetFloat(
            GameSettings.ZoomSpeedPrefKey,
            GameSettings.DefaultZoomSpeed
        );
        _zoomSnap = new SnapSlider(
            GameSettings.MinZoomSpeed,
            GameSettings.MaxZoomSpeed,
            savedZoom,
            smallStep: 0.1f,
            snapStep: 0f,
            format: "F1",
            showLock: false
        );
        _zoomSnap.OnValueChanged += val => PlayerPrefs.SetFloat(GameSettings.ZoomSpeedPrefKey, val);
        _zoomSnap.Root.AddToClassList("setting-snap-slider");
        _settings.Q("zoom-speed-row").Add(_zoomSnap.Root);

        var themeChoices = new System.Collections.Generic.List<string>();
        foreach (var t in ThemeManager.Available)
            if (t != null)
                themeChoices.Add(t.name);
        _themeDropdown = new CustomDropdown(
            themeChoices,
            ThemeManager.Current != null ? ThemeManager.Current.name : ""
        );
        _themeDropdown.ValueChanged += name =>
        {
            foreach (var t in ThemeManager.Available)
                if (t != null && t.name == name)
                {
                    ThemeManager.Apply(t);
                    break;
                }
        };
        _settings.Q("theme-dropdown-slot").Add(_themeDropdown.Root);

        // Keybinds section.
        var keybindList = _settings.Q("keybind-list");
        if (keybindList != null)
            _keybindSection = new KeybindSettingsSection(keybindList);

        // Keep trail after clear toggle.
        var keepTrailToggle = _settings.Q<Toggle>("keep-trail-toggle");
        if (keepTrailToggle != null)
        {
            keepTrailToggle.value = PlayerPrefs.GetInt(GameSettings.KeepTrailPrefKey, 0) == 1;
            keepTrailToggle.RegisterValueChangedCallback(evt =>
            {
                PlayerPrefs.SetInt(GameSettings.KeepTrailPrefKey, evt.newValue ? 1 : 0);
                PlayerPrefs.Save();
            });
        }
    }

    private void WireSettingsNav()
    {
        var settingsScroll = _settings.Q<ScrollView>("settings-scroll");
        var sectionAccount = _settings.Q("section-account");
        var sectionGameplay = _settings.Q("section-gameplay");
        var sectionKeybinds = _settings.Q("section-keybinds");
        var sectionData = _settings.Q("section-data");
        var sectionAbout = _settings.Q("section-about");

        var navAccount = _settings.Q<Button>("nav-account");
        var navGameplay = _settings.Q<Button>("nav-gameplay");
        var navKeybinds = _settings.Q<Button>("nav-keybinds");
        var navData = _settings.Q<Button>("nav-data");
        var navAbout = _settings.Q<Button>("nav-about");
        Button[] navBtns = { navAccount, navGameplay, navKeybinds, navData, navAbout };
        VisualElement[] sections =
        {
            sectionAccount,
            sectionGameplay,
            sectionKeybinds,
            sectionData,
            sectionAbout,
        };

        navAccount.clicked += () =>
        {
            _navScrollPending = true;
            ScrollToSection(settingsScroll, sectionAccount);
            SetNavActive(navBtns, 0);
        };
        navGameplay.clicked += () =>
        {
            _navScrollPending = true;
            ScrollToSection(settingsScroll, sectionGameplay);
            SetNavActive(navBtns, 1);
        };
        navKeybinds.clicked += () =>
        {
            _navScrollPending = true;
            ScrollToSection(settingsScroll, sectionKeybinds);
            SetNavActive(navBtns, 2);
        };
        navData.clicked += () =>
        {
            _navScrollPending = true;
            ScrollToSection(settingsScroll, sectionData);
            SetNavActive(navBtns, 3);
        };
        navAbout.clicked += () =>
        {
            _navScrollPending = true;
            ScrollToSection(settingsScroll, sectionAbout);
            SetNavActive(navBtns, 4);
        };

        _settings.Q<Label>("about-version").text = $"v{Application.version} ({GitCommitHash()})";
        _settings.Q<Button>("about-github-btn").clicked += () =>
            ExternalLinks.Open(ExternalLinks.GitHub);
        _settings.Q<Button>("about-discord-btn").clicked += () =>
            ExternalLinks.Open(ExternalLinks.Discord);

        settingsScroll.verticalScroller.valueChanged += _ =>
        {
            if (_navScrollPending)
            {
                _navScrollPending = false;
                return;
            }
            if (settingsScroll.verticalScroller.highValue > 8)
                UpdateSettingsNavActive(settingsScroll, sections, navBtns);
        };

        _settings.Q<Button>("settings-close-btn").clicked += Close;
        _settings.Q("settings-backdrop").RegisterCallback<PointerDownEvent>(_ => Close());
        _settings.Q<Button>("clear-scores-btn").clicked += OnClearScores;
    }

    private void WireModals(VisualElement root)
    {
        _clearScoresModal = new ConfirmModal(
            root.Q("clear-scores-modal"),
            "Delete all non-favorited local scores?",
            "Delete",
            "Cancel",
            subtitle: "Favorited entries will be kept.",
            isDanger: true
        );
        _clearScoresModal.Confirmed += OnClearScoresConfirm;
        _clearScoresModal.Cancelled += () => _clearScoresModal.Hide();

        _externalLinkModal = new ConfirmModal(
            root.Q("external-link-modal"),
            "Open external link?",
            "Open",
            "Cancel",
            subtitle: ""
        );
        _externalLinkLabel = root.Q("external-link-modal").Q<Label>("modal-subtitle");
    }

    // -- Callbacks -----------------------------------------------------------

    private void OnClearScores() => _clearScoresModal.Show();

    private void OnClearScoresConfirm()
    {
        _clearScoresModal.Hide();
        LeaderboardManager.Instance.RemoveAllNonFavorited();
    }

    private void OnExternalLinkRequested(string url, Action confirm)
    {
        _externalLinkLabel.text = url;
        _externalLinkModal.Show();
        _externalLinkModal.Confirmed += OnConfirm;
        _externalLinkModal.Cancelled += OnCancel;

        void Cleanup()
        {
            _externalLinkModal.Confirmed -= OnConfirm;
            _externalLinkModal.Cancelled -= OnCancel;
        }

        void OnConfirm()
        {
            Cleanup();
            _externalLinkModal.Hide();
            confirm();
        }

        void OnCancel()
        {
            Cleanup();
            _externalLinkModal.Hide();
        }
    }

    // -- Helpers ------------------------------------------------------------

    private static void ScrollToSection(ScrollView scroll, VisualElement section)
    {
        scroll.schedule.Execute(() => scroll.verticalScroller.value = section.layout.y);
    }

    private static void UpdateSettingsNavActive(
        ScrollView scroll,
        VisualElement[] sections,
        Button[] navBtns
    )
    {
        float scrollY = scroll.verticalScroller.value;
        int active = 0;
        if (scrollY >= scroll.verticalScroller.highValue - 8)
        {
            active = sections.Length - 1;
        }
        else
        {
            for (int i = sections.Length - 1; i >= 0; i--)
            {
                if (scrollY >= sections[i].layout.y - 8)
                {
                    active = i;
                    break;
                }
            }
        }
        SetNavActive(navBtns, active);
    }

    private static void SetNavActive(Button[] navBtns, int active)
    {
        for (int i = 0; i < navBtns.Length; i++)
        {
            if (i == active)
                navBtns[i].AddToClassList("settings-nav-btn--active");
            else
                navBtns[i].RemoveFromClassList("settings-nav-btn--active");
        }
    }

    private static void SetVisible(VisualElement el, bool visible)
    {
        if (visible)
            el.RemoveFromClassList("screen--hidden");
        else
            el.AddToClassList("screen--hidden");
    }

    private static string GitCommitHash()
    {
        var asset = Resources.Load<TextAsset>("git-commit");
        return asset != null ? asset.text.Trim() : "dev";
    }
}
