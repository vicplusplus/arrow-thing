using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Scene controller for the Solo Size Select screen. Manages board-size preset
/// selection, custom sliders, and keyboard navigation.
/// </summary>
public sealed class SoloSizeSelectController : NavigableScene
{
    private const int CustomDimMin = 2;
    private const int CustomDimMax = 400;

    // Preset buttons
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

    // Nav graph indices (set in BuildNavGraph, used in LinkPresetGrid)
    private int _startIdx;
    private int _presetBase;
    private int _backIdx;
    private int _trophyIdx;

    protected override KeybindManager.Context NavContext => KeybindManager.Context.ModeSelect;

    protected override void BuildUI(VisualElement root)
    {
        _presetSmall = root.Q<Button>("preset-small");
        _presetMedium = root.Q<Button>("preset-medium");
        _presetLarge = root.Q<Button>("preset-large");
        _presetXLarge = root.Q<Button>("preset-xlarge");

        _presetSmall.clicked += () => SelectPreset(10, 10);
        _presetMedium.clicked += () => SelectPreset(20, 20);
        _presetLarge.clicked += () => SelectPreset(40, 40);
        _presetXLarge.clicked += () => SelectPreset(100, 100);

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

        _presetCustom.clicked += SelectCustom;
        root.Q<Button>("start-btn").clicked += OnStart;
        root.Q<Button>("back-btn").clicked += OnBack;

        var trophyBtn = root.Q<Button>("trophy-btn");
        if (trophyBtn != null)
            trophyBtn.clicked += OnLeaderboard;

        // Restore selection from GameSettings (runs once on scene load, not on
        // nav rebuilds, so it won't undo user clicks that trigger a rebuild).
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
    }

    protected override void BuildNavGraph(FocusNavigator nav)
    {
        var items = new List<FocusNavigator.FocusItem>();

        _backIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("back-btn"),
                OnActivate = () =>
                {
                    OnBack();
                    return true;
                },
            }
        );

        _trophyIdx = items.Count;
        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("trophy-btn"),
                OnActivate = () =>
                {
                    OnLeaderboard();
                    return true;
                },
            }
        );

        _presetBase = items.Count;
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

        items.Add(
            new FocusNavigator.FocusItem
            {
                Element = Root.Q<Button>("start-btn"),
                OnActivate = () =>
                {
                    OnStart();
                    return true;
                },
            }
        );

        _startIdx = items.Count - 1;
        nav.SetItems(items, GetPresetIndex());

        // Grid links built after layout resolves. Unregister first to avoid
        // stacking callbacks when RebuildNavigator re-enters BuildNavGraph.
        var presetGrid = Root.Q(className: "preset-grid");
        if (presetGrid != null)
        {
            presetGrid.UnregisterCallback<GeometryChangedEvent>(OnPresetGridLayout);
            presetGrid.RegisterCallback<GeometryChangedEvent>(OnPresetGridLayout);
        }
    }

    private void OnPresetGridLayout(GeometryChangedEvent evt) => LinkPresetGrid();

    private void LinkPresetGrid()
    {
        Navigator.ClearLinks();

        int b = _presetBase;
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
        int belowPresets = _isCustomSelected ? customIdx + 1 : _startIdx;
        foreach (int idx in lastPresetRow)
            Navigator.LinkBidi(idx, FocusNavigator.NavDir.Down, belowPresets);

        if (_isCustomSelected)
            Navigator.LinkChain(customIdx, _startIdx - customIdx + 1);
        else
            Navigator.LinkBidi(customIdx, FocusNavigator.NavDir.Down, _startIdx);

        var topRow = rows[0];
        int topLeft = topRow[0];
        int topRight = topRow[topRow.Count - 1];

        Navigator.Link(_backIdx, FocusNavigator.NavDir.Down, topLeft);
        Navigator.Link(_backIdx, FocusNavigator.NavDir.Right, topLeft);
        Navigator.Link(_trophyIdx, FocusNavigator.NavDir.Down, topRight);
        Navigator.Link(_trophyIdx, FocusNavigator.NavDir.Left, topRight);
        Navigator.Link(topLeft, FocusNavigator.NavDir.Up, _backIdx);
        Navigator.Link(topLeft, FocusNavigator.NavDir.Left, _backIdx);
        for (int i = 1; i < topRow.Count; i++)
            Navigator.Link(topRow[i], FocusNavigator.NavDir.Up, _trophyIdx);
    }

    private int GetPresetIndex()
    {
        int b = _presetBase;
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
        Debug.LogWarning(
            $"[SoloSizeSelect] GetPresetIndex: no preset matches {_selectedWidth}x{_selectedHeight}, defaulting to Small"
        );
        return b;
    }

    protected override void OnUpdate(KeybindManager km)
    {
        if (km.OpenLeaderboard != null && km.OpenLeaderboard.WasPerformedThisFrame())
            OnLeaderboard();
    }

    protected override void OnCancel() => OnBack();

    // -- Selection ----------------------------------------------------------------

    private void SelectPreset(int width, int height)
    {
        bool wasCustom = _isCustomSelected;
        _isCustomSelected = false;
        _selectedWidth = width;
        _selectedHeight = height;
        SetVisible(_customPanel, false);
        UpdateAllPresetHighlights();
        if (wasCustom)
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
        if (!wasCustom)
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

    private void OnStart()
    {
        GameSettings.Apply(_selectedWidth, _selectedHeight);
        SceneNav.Push("Game");
    }

    private void OnBack() => SceneNav.Pop();

    private void OnLeaderboard() => SceneNav.Push("Leaderboard");

    private static void SetVisible(VisualElement el, bool visible)
    {
        if (visible)
            el.RemoveFromClassList("screen--hidden");
        else
            el.AddToClassList("screen--hidden");
    }
}
