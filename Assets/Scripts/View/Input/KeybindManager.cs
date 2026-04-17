using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Singleton that owns all keyboard shortcut action maps and manages context-based
/// enabling/disabling. Creates its own InputActionAsset at runtime (separate from the
/// game's existing InputSystem_Actions asset) to avoid conflicts with Unity's built-in
/// UI integration. Persists binding overrides to PlayerPrefs. Bootstraps automatically
/// via RuntimeInitializeOnLoadMethod — no prefab required.
/// </summary>
public sealed class KeybindManager : MonoBehaviour
{
    public static KeybindManager Instance { get; private set; }

    /// <summary>
    /// Contexts determine which shortcut action maps are active. Navigation
    /// actions (Navigate/Submit/Cancel/Tab) are always enabled regardless of context.
    /// </summary>
    public enum Context
    {
        None,
        MainMenu,
        ModeSelect,
        Settings,
        Gameplay,
        Leaderboard,
        Replay,
        CoopHub,
    }

    private InputActionAsset _asset;

    // Action maps.
    private InputActionMap _navMap;
    private InputActionMap _modeSelectMap;
    private InputActionMap _gameplayMap;
    private InputActionMap _leaderboardMap;

    // Navigation actions (always active).
    public InputAction Navigate { get; private set; }
    public InputAction Submit { get; private set; }
    public InputAction Cancel { get; private set; }
    public InputAction Tab { get; private set; }
    public InputAction ToggleSettings { get; private set; }

    // Pointer/gameplay input (always active).
    public InputAction Point { get; private set; }
    public InputAction Select { get; private set; }
    public InputAction Zoom { get; private set; }

    // ModeSelect shortcuts.
    public InputAction OpenLeaderboard { get; private set; }

    // Gameplay shortcuts.
    public InputAction QuickReset { get; private set; }
    public InputAction ToggleTrail { get; private set; }
    public InputAction ClickHovered { get; private set; }
    public InputAction QuickSave { get; private set; }

    // Leaderboard shortcuts.
    public InputAction TabSmall { get; private set; }
    public InputAction TabMedium { get; private set; }
    public InputAction TabLarge { get; private set; }
    public InputAction TabXLarge { get; private set; }
    public InputAction TabAll { get; private set; }
    public InputAction ToggleFavorites { get; private set; }
    public InputAction SwapGlobal { get; private set; }

    public bool IsRebinding
    {
        get => _isRebinding;
        set
        {
            if (_isRebinding == value)
                return;
            _isRebinding = value;
            ApplyContext();
        }
    }
    private bool _isRebinding;

    private Context _activeContext;
    private bool _textFieldFocused;

    public Context ActiveContext
    {
        get => _activeContext;
        set
        {
            if (_activeContext == value)
                return;
            _activeContext = value;
            ApplyContext();
        }
    }

    public bool TextFieldFocused
    {
        get => _textFieldFocused;
        set
        {
            if (_textFieldFocused == value)
                return;
            _textFieldFocused = value;
            ApplyContext();
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null)
            return;
        var go = new GameObject("KeybindManager");
        go.AddComponent<KeybindManager>();
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        BuildActions();
        LoadBindingOverrides();
        _navMap.Enable();
        ApplyContext();
    }

    private void Start()
    {
        // UI Toolkit creates an internal DefaultEventSystem that maps WASD to
        // navigation when no scene EventSystem exists. We create one with an
        // InputSystemUIInputModule (for pointer/touch processing) but with
        // navigation disabled. DontDestroyOnLoad keeps it alive across scene loads.
        if (UnityEngine.EventSystems.EventSystem.current == null)
        {
            var esGo = new GameObject("EventSystem (nav disabled)");
            esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
            var uiModule = esGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            // Clear actions so they don't generate navigation events or consume keys.
            uiModule.move = null;
            uiModule.submit = null;
            uiModule.cancel = null;
            DontDestroyOnLoad(esGo);
        }
        UnityEngine.EventSystems.EventSystem.current.sendNavigationEvents = false;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    private readonly HashSet<UnityEngine.UIElements.IPanel> _watchedPanels =
        new HashSet<UnityEngine.UIElements.IPanel>();

    private void Update()
    {
        // Watch all active UIDocuments for navigation events that slip through
        // our suppression. Logs a warning so we catch regressions immediately.
        foreach (
            var doc in FindObjectsByType<UnityEngine.UIElements.UIDocument>(
                FindObjectsSortMode.None
            )
        )
        {
            if (doc.rootVisualElement == null)
                continue;
            var panel = doc.rootVisualElement.panel;
            if (panel == null || _watchedPanels.Contains(panel))
                continue;
            _watchedPanels.Add(panel);

            var root = doc.rootVisualElement;
            root.RegisterCallback<UnityEngine.UIElements.NavigationMoveEvent>(WarnNav);
            root.RegisterCallback<UnityEngine.UIElements.NavigationSubmitEvent>(WarnNav);
            root.RegisterCallback<UnityEngine.UIElements.NavigationCancelEvent>(WarnNav);
        }
    }

    private static void WarnNav<T>(T evt)
        where T : UnityEngine.UIElements.EventBase<T>, new()
    {
        Debug.LogWarning(
            $"[KeybindManager] Unity navigation event reached target: {evt.GetType().Name} "
                + $"→ {evt.target} (propagation not suppressed)"
        );
    }
#endif

    // -- Action construction --------------------------------------------------

    private void BuildActions()
    {
        _asset = ScriptableObject.CreateInstance<InputActionAsset>();

        BuildNavMap();
        BuildModeSelectMap();
        BuildGameplayMap();
        BuildLeaderboardMap();
    }

    // Stable GUIDs for all rebindable bindings so overrides persist across sessions.
    // Navigation/pointer bindings don't need stable IDs since they're not rebindable.
    private static readonly System.Guid IdOpenLeaderboard = System.Guid.Parse(
        "b0000001-0000-0000-0000-000000000001"
    );
    private static readonly System.Guid IdQuickReset = System.Guid.Parse(
        "b0000002-0000-0000-0000-000000000001"
    );
    private static readonly System.Guid IdToggleTrail = System.Guid.Parse(
        "b0000002-0000-0000-0000-000000000002"
    );
    private static readonly System.Guid IdClickHovered = System.Guid.Parse(
        "b0000002-0000-0000-0000-000000000003"
    );
    private static readonly System.Guid IdQuickSave = System.Guid.Parse(
        "b0000002-0000-0000-0000-000000000004"
    );
    private static readonly System.Guid IdTabSmall = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000001"
    );
    private static readonly System.Guid IdTabMedium = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000002"
    );
    private static readonly System.Guid IdTabLarge = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000003"
    );
    private static readonly System.Guid IdTabXLarge = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000004"
    );
    private static readonly System.Guid IdTabAll = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000005"
    );
    private static readonly System.Guid IdToggleFavorites = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000006"
    );
    private static readonly System.Guid IdSwapGlobal = System.Guid.Parse(
        "b0000003-0000-0000-0000-000000000007"
    );

    private static void AddStableBinding(InputAction action, string path, System.Guid id)
    {
        action.AddBinding(
            new InputBinding
            {
                path = path,
                id = id,
                action = action.name,
            }
        );
    }

    private void BuildNavMap()
    {
        _navMap = _asset.AddActionMap("Navigation");

        Navigate = _navMap.AddAction("Navigate", InputActionType.PassThrough);
        Navigate.expectedControlType = "Vector2";
        var composite = Navigate.AddCompositeBinding("2DVector");
        composite.With("Up", "<Keyboard>/upArrow");
        composite.With("Down", "<Keyboard>/downArrow");
        composite.With("Left", "<Keyboard>/leftArrow");
        composite.With("Right", "<Keyboard>/rightArrow");

        Submit = _navMap.AddAction("Submit", InputActionType.Button);
        Submit.AddBinding("<Keyboard>/enter");
        Submit.AddBinding("<Keyboard>/numpadEnter");

        Cancel = _navMap.AddAction("Cancel", InputActionType.Button);
        Cancel.AddBinding("<Keyboard>/escape");

        Tab = _navMap.AddAction("Tab", InputActionType.Button);
        Tab.AddBinding("<Keyboard>/tab");

        ToggleSettings = _navMap.AddAction("ToggleSettings", InputActionType.Button);
        ToggleSettings.AddBinding("<Keyboard>/o");

        Point = _navMap.AddAction("Point", InputActionType.PassThrough);
        Point.expectedControlType = "Vector2";
        Point.AddBinding("<Mouse>/position").WithGroup("Keyboard&Mouse");
        Point.AddBinding("<Touchscreen>/position").WithGroup("Touch");

        Select = _navMap.AddAction("Select", InputActionType.Button);
        Select.AddBinding("<Mouse>/leftButton").WithGroup("Keyboard&Mouse");
        Select.AddBinding("<Touchscreen>/Press").WithGroup("Touch");

        Zoom = _navMap.AddAction("Zoom", InputActionType.PassThrough);
        Zoom.expectedControlType = "Axis";
        Zoom.AddBinding("<Mouse>/scroll/y").WithGroup("Keyboard&Mouse");
    }

    private void BuildModeSelectMap()
    {
        _modeSelectMap = _asset.AddActionMap("Shortcuts_ModeSelect");

        OpenLeaderboard = _modeSelectMap.AddAction("OpenLeaderboard", InputActionType.Button);
        AddStableBinding(OpenLeaderboard, "<Keyboard>/l", IdOpenLeaderboard);
    }

    private void BuildGameplayMap()
    {
        _gameplayMap = _asset.AddActionMap("Shortcuts_Gameplay");

        QuickReset = _gameplayMap.AddAction("QuickReset", InputActionType.Button);
        AddStableBinding(QuickReset, "<Keyboard>/r", IdQuickReset);

        ToggleTrail = _gameplayMap.AddAction("ToggleTrail", InputActionType.Button);
        AddStableBinding(ToggleTrail, "<Keyboard>/t", IdToggleTrail);

        ClickHovered = _gameplayMap.AddAction("ClickHovered", InputActionType.Button);
        AddStableBinding(ClickHovered, "<Keyboard>/space", IdClickHovered);

        QuickSave = _gameplayMap.AddAction("QuickSave", InputActionType.Button);
        AddStableBinding(QuickSave, "<Keyboard>/s", IdQuickSave);
    }

    private void BuildLeaderboardMap()
    {
        _leaderboardMap = _asset.AddActionMap("Shortcuts_Leaderboard");

        TabSmall = _leaderboardMap.AddAction("TabSmall", InputActionType.Button);
        AddStableBinding(TabSmall, "<Keyboard>/1", IdTabSmall);

        TabMedium = _leaderboardMap.AddAction("TabMedium", InputActionType.Button);
        AddStableBinding(TabMedium, "<Keyboard>/2", IdTabMedium);

        TabLarge = _leaderboardMap.AddAction("TabLarge", InputActionType.Button);
        AddStableBinding(TabLarge, "<Keyboard>/3", IdTabLarge);

        TabXLarge = _leaderboardMap.AddAction("TabXLarge", InputActionType.Button);
        AddStableBinding(TabXLarge, "<Keyboard>/4", IdTabXLarge);

        TabAll = _leaderboardMap.AddAction("TabAll", InputActionType.Button);
        AddStableBinding(TabAll, "<Keyboard>/5", IdTabAll);

        ToggleFavorites = _leaderboardMap.AddAction("ToggleFavorites", InputActionType.Button);
        AddStableBinding(ToggleFavorites, "<Keyboard>/f", IdToggleFavorites);

        SwapGlobal = _leaderboardMap.AddAction("SwapGlobal", InputActionType.Button);
        AddStableBinding(SwapGlobal, "<Keyboard>/l", IdSwapGlobal);
    }

    // -- Context switching -----------------------------------------------------

    private void ApplyContext()
    {
        bool suppress = _textFieldFocused || _isRebinding;

        _modeSelectMap.Disable();
        _gameplayMap.Disable();
        _leaderboardMap.Disable();

        if (suppress)
            return;

        switch (_activeContext)
        {
            case Context.MainMenu:
            case Context.ModeSelect:
                _modeSelectMap.Enable();
                break;
            case Context.Gameplay:
                _gameplayMap.Enable();
                break;
            case Context.Leaderboard:
                _leaderboardMap.Enable();
                break;
        }
    }

    // -- Binding overrides persistence ----------------------------------------

    // Bump this when binding IDs change to invalidate stale overrides.
    private const int BindingVersion = 1;
    private const string BindingVersionKey = "InputBindingVersion";

    private void LoadBindingOverrides()
    {
        int saved = PlayerPrefs.GetInt(BindingVersionKey, 0);
        if (saved != BindingVersion)
        {
            // Binding IDs changed — discard stale overrides.
            PlayerPrefs.DeleteKey(GameSettings.InputBindingOverridesPrefKey);
            PlayerPrefs.SetInt(BindingVersionKey, BindingVersion);
            PlayerPrefs.Save();
            return;
        }

        string json = PlayerPrefs.GetString(GameSettings.InputBindingOverridesPrefKey, "");
        if (!string.IsNullOrEmpty(json))
            _asset.LoadBindingOverridesFromJson(json);
    }

    public void SaveBindingOverrides()
    {
        string json = _asset.SaveBindingOverridesAsJson();
        PlayerPrefs.SetString(GameSettings.InputBindingOverridesPrefKey, json);
        PlayerPrefs.Save();
    }

    public void ResetAllBindings()
    {
        _asset.RemoveAllBindingOverrides();
        PlayerPrefs.DeleteKey(GameSettings.InputBindingOverridesPrefKey);
        PlayerPrefs.Save();
    }

    public void ResetBindingsForAction(InputAction action)
    {
        action.RemoveAllBindingOverrides();
        SaveBindingOverrides();
    }

    // -- Conflict detection ---------------------------------------------------

    public List<(string actionName, string displayString)> FindConflicts(
        InputAction excludeAction,
        string bindingPath
    )
    {
        var conflicts = new List<(string, string)>();
        if (string.IsNullOrEmpty(bindingPath))
            return conflicts;

        InputActionMap targetMap = excludeAction.actionMap;

        foreach (var action in targetMap.actions)
        {
            if (action == excludeAction)
                continue;
            for (int i = 0; i < action.bindings.Count; i++)
            {
                var binding = action.bindings[i];
                if (binding.isComposite || binding.isPartOfComposite)
                    continue;
                string effectivePath = binding.hasOverrides ? binding.overridePath : binding.path;
                if (string.Equals(effectivePath, bindingPath, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add((action.name, action.GetBindingDisplayString(i)));
                }
            }
        }
        return conflicts;
    }

    public static string GetBindingDisplayString(InputAction action)
    {
        for (int i = 0; i < action.bindings.Count; i++)
        {
            var binding = action.bindings[i];
            if (!binding.isComposite && !binding.isPartOfComposite)
                return action.GetBindingDisplayString(i);
        }
        return action.GetBindingDisplayString(0);
    }

    public static int GetRebindableBindingIndex(InputAction action)
    {
        for (int i = 0; i < action.bindings.Count; i++)
        {
            var binding = action.bindings[i];
            if (!binding.isComposite && !binding.isPartOfComposite)
                return i;
        }
        return 0;
    }

    public List<(string groupName, List<InputAction> actions)> GetRebindableActions()
    {
        var result = new List<(string, List<InputAction>)>();

        result.Add(("Board Select", GetActionsFromMap(_modeSelectMap)));
        result.Add(("Gameplay", GetActionsFromMap(_gameplayMap)));
        result.Add(("Leaderboard", GetActionsFromMap(_leaderboardMap)));

        return result;
    }

    private static List<InputAction> GetActionsFromMap(InputActionMap map)
    {
        var actions = new List<InputAction>();
        foreach (var action in map.actions)
            actions.Add(action);
        return actions;
    }
}
