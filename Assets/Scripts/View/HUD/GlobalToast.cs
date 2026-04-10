using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Singleton toast overlay that persists across scene transitions.
/// Shows messages with a dismiss button. Error toasts persist until
/// manually dismissed; success/info toasts auto-hide after a delay.
/// Retryable errors show an additional Retry button.
///
/// Bootstrap: place a prefab at Resources/GlobalToast with a UIDocument
/// (pointing to GlobalToast.uxml), a UIThemeApplier, and this component.
/// The UIDocument's PanelSettings sort order must be higher than all
/// other UI (including SettingsController) so toasts render on top.
/// </summary>
public sealed class GlobalToast : MonoBehaviour
{
    public static GlobalToast Instance { get; private set; }

    private VisualElement _toast;
    private Label _text;
    private Button _retryBtn;
    private Button _dismissBtn;
    private float _autoHideTime;
    private bool _autoHidePending;
    private Action _retryAction;

    private const float AutoHideDuration = 4f;
    private const string HiddenClass = "global-toast--hidden";

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (Instance != null)
            return;

        var prefab = Resources.Load<GameObject>("GlobalToast");
        if (prefab == null)
        {
            Debug.LogError(
                "[GlobalToast] Prefab not found at Resources/GlobalToast. "
                    + "Create a prefab there with UIDocument, UIThemeApplier, and GlobalToast."
            );
            return;
        }
        var go = Instantiate(prefab);
        go.name = "GlobalToast";
        DontDestroyOnLoad(go);
    }

    private void Awake()
    {
        Instance = this;
    }

    private void Start()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        _toast = root.Q("global-toast");
        _text = root.Q<Label>("global-toast-text");
        _retryBtn = root.Q<Button>("global-toast-retry");
        _dismissBtn = root.Q<Button>("global-toast-dismiss");

        if (_retryBtn != null)
            _retryBtn.clicked += OnRetry;
        if (_dismissBtn != null)
            _dismissBtn.clicked += Dismiss;
    }

    private void Update()
    {
        if (_autoHidePending && Time.unscaledTime >= _autoHideTime)
        {
            _autoHidePending = false;
            Hide();
        }
    }

    /// <summary>
    /// Shows an error toast that persists until the user dismisses it.
    /// No retry option. Survives scene transitions.
    /// </summary>
    public void ShowError(string message)
    {
        Show(message, persistent: true, retryAction: null);
    }

    /// <summary>
    /// Shows an error toast with a Retry button. Persists until the user
    /// dismisses or retries. Survives scene transitions.
    /// </summary>
    public void ShowRetryableError(string message, Action retryAction)
    {
        Show(message, persistent: true, retryAction: retryAction);
    }

    /// <summary>
    /// Shows an informational toast that auto-hides after a few seconds.
    /// </summary>
    public void ShowInfo(string message)
    {
        Show(message, persistent: false, retryAction: null);
    }

    /// <summary>Hides the toast immediately.</summary>
    public void Dismiss()
    {
        _autoHidePending = false;
        _retryAction = null;
        Hide();
    }

    private void OnRetry()
    {
        var action = _retryAction;
        Dismiss();
        action?.Invoke();
    }

    private void Show(string message, bool persistent, Action retryAction)
    {
        if (_toast == null)
            return;

        _text.text = message;
        _retryAction = retryAction;
        _toast.RemoveFromClassList(HiddenClass);

        // Retry button: visible only when a retry action is provided
        if (_retryBtn != null)
        {
            if (retryAction != null)
                _retryBtn.RemoveFromClassList(HiddenClass);
            else
                _retryBtn.AddToClassList(HiddenClass);
        }

        // Dismiss button: always visible for persistent toasts, hidden for auto-dismiss
        if (_dismissBtn != null)
        {
            if (persistent)
                _dismissBtn.RemoveFromClassList(HiddenClass);
            else
                _dismissBtn.AddToClassList(HiddenClass);
        }

        if (persistent)
        {
            _autoHidePending = false;
        }
        else
        {
            _autoHidePending = true;
            _autoHideTime = Time.unscaledTime + AutoHideDuration;
        }
    }

    private void Hide()
    {
        if (_toast != null)
            _toast.AddToClassList(HiddenClass);
    }
}
