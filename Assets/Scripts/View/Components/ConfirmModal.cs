using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

/// <summary>
/// Wraps a ConfirmModal.uxml template instance and configures its content.
/// Keyboard navigation uses FocusNavigator push/pop: default focus on Cancel,
/// Left/Right to toggle, Enter to activate, Escape to cancel (or dismiss).
///
/// <b>Event lifetime:</b> Callers subscribe to <see cref="Confirmed"/>,
/// <see cref="Cancelled"/>, and <see cref="Dismissed"/> once after construction.
/// These subscriptions persist for the modal's lifetime. If a modal is reused
/// across different contexts, callers must unsubscribe before re-subscribing to
/// avoid duplicate invocations. <see cref="Hide"/> does not clear event handlers.
///
/// When <c>isDismissable</c> is true, an X button is added and Escape triggers
/// <see cref="Dismissed"/> instead of <see cref="Cancelled"/>, giving a third
/// distinct action (e.g. "go back to what I was doing" vs the Cancel button text).
/// </summary>
public class ConfirmModal
{
    private readonly VisualElement _root;
    private readonly VisualElement _overlay;
    private readonly Button _confirmBtn;
    private readonly Button _cancelBtn;
    private Button _dismissBtn;
    private bool _isDismissable;

    public event Action Confirmed;
    public event Action Cancelled;

    /// <summary>
    /// Fired when the modal is dismissed via the X button or Escape.
    /// Only used when the modal was created with <c>isDismissable: true</c>.
    /// </summary>
    public event Action Dismissed;

    /// <summary>True when the modal is currently visible.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>
    /// Reconfigure the modal's text and behavior for reuse with different
    /// contexts (e.g. a single leave modal that switches between simple
    /// "Leave?" and "Save before leaving?" modes).
    /// </summary>
    public void Reconfigure(
        string title,
        string confirmText,
        string cancelText,
        string subtitle = null,
        bool isDismissable = false,
        bool isDanger = false
    )
    {
        _root.Q<Label>("modal-title").text = title;
        _confirmBtn.text = confirmText;
        _cancelBtn.text = cancelText;

        if (isDanger)
            _confirmBtn.AddToClassList("modal-btn--danger");
        else
            _confirmBtn.RemoveFromClassList("modal-btn--danger");

        var sub = _root.Q<Label>("modal-subtitle");
        if (sub != null)
        {
            if (subtitle != null)
            {
                sub.text = subtitle;
                sub.RemoveFromClassList("screen--hidden");
            }
            else
            {
                sub.AddToClassList("screen--hidden");
            }
        }

        _isDismissable = isDismissable;
        // Re-add or remove the dismiss button as needed.
        if (isDismissable && _dismissBtn == null)
        {
            var modalBox = _overlay.Q(className: "modal-box");
            _dismissBtn = new Button();
            _dismissBtn.AddToClassList("modal-close-btn");
            var icon = new VisualElement();
            icon.AddToClassList("modal-close-btn__icon");
            icon.AddToClassList("icon--close");
            _dismissBtn.Add(icon);
            _dismissBtn.clicked += () => Dismissed?.Invoke();
            modalBox.Insert(0, _dismissBtn);
        }
        else if (!isDismissable && _dismissBtn != null)
        {
            _dismissBtn.RemoveFromHierarchy();
            _dismissBtn = null;
        }
    }

    public ConfirmModal(
        VisualElement root,
        string title,
        string confirmText,
        string cancelText,
        string subtitle = null,
        bool isDanger = false,
        bool isDismissable = false
    )
    {
        _root = root;
        _isDismissable = isDismissable;
        _overlay = root.ClassListContains("modal-overlay")
            ? root
            : root.Q(className: "modal-overlay");

        root.Q<Label>("modal-title").text = title;

        if (subtitle != null)
        {
            var sub = root.Q<Label>("modal-subtitle");
            sub.text = subtitle;
            sub.RemoveFromClassList("screen--hidden");
        }

        _confirmBtn = root.Q<Button>("modal-confirm-btn");
        _confirmBtn.text = confirmText;
        if (isDanger)
            _confirmBtn.AddToClassList("modal-btn--danger");

        _cancelBtn = root.Q<Button>("modal-cancel-btn");
        _cancelBtn.text = cancelText;

        _confirmBtn.clicked += () => Confirmed?.Invoke();
        _cancelBtn.clicked += () => Cancelled?.Invoke();

        // Start hidden.
        _root.style.display = DisplayStyle.None;

        // Dismissable: add an X button at the top-right of the modal box.
        if (isDismissable)
        {
            var modalBox = _overlay.Q(className: "modal-box");
            _dismissBtn = new Button();
            _dismissBtn.AddToClassList("modal-close-btn");
            var icon = new VisualElement();
            icon.AddToClassList("modal-close-btn__icon");
            icon.AddToClassList("icon--close");
            _dismissBtn.Add(icon);
            _dismissBtn.clicked += () => Dismissed?.Invoke();
            modalBox.Insert(0, _dismissBtn);
        }
    }

    public void Show()
    {
        IsVisible = true;
        _root.style.display = DisplayStyle.Flex;
        _overlay.RemoveFromClassList("screen--hidden");

        var nav = FocusNavigator.Active;
        if (nav != null)
        {
            var items = new List<FocusNavigator.FocusItem>
            {
                new FocusNavigator.FocusItem
                {
                    Element = _confirmBtn,
                    OnActivate = () =>
                    {
                        Confirmed?.Invoke();
                        return true;
                    },
                },
                new FocusNavigator.FocusItem
                {
                    Element = _cancelBtn,
                    OnActivate = () =>
                    {
                        Cancelled?.Invoke();
                        return true;
                    },
                },
            };

            Func<bool> onCancel;
            if (_isDismissable)
                onCancel = () =>
                {
                    Dismissed?.Invoke();
                    return true;
                };
            else
                onCancel = () =>
                {
                    Cancelled?.Invoke();
                    return true;
                };

            nav.PushModal(items, initialIndex: 1, onCancel: onCancel);
            nav.LinkRow(0, items.Count);
            nav.SetFocus(1);
        }
    }

    public void Hide()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        _overlay.AddToClassList("screen--hidden");
        _root.style.display = DisplayStyle.None;

        var nav = FocusNavigator.Active;
        if (nav != null && nav.HasModal)
            nav.PopModal();
    }
}
