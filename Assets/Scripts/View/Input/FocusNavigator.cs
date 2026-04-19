using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

/// <summary>
/// Manages keyboard focus navigation for a list of UI Toolkit elements.
/// Adds/removes a <c>kb-focused</c> CSS class to show a visual focus ring.
/// Hides the ring on any pointer event; re-shows on next keyboard input.
///
/// Create one per screen/panel. Call <see cref="SetItems"/> with the focusable
/// elements, then call <see cref="Update"/> every frame.
/// </summary>
public sealed class FocusNavigator
{
    /// <summary>
    /// Optional per-item callback invoked when the user presses Submit (Enter)
    /// on a focused item. If null, the default activation logic runs (click for
    /// buttons, etc.).
    /// </summary>
    public struct FocusItem
    {
        public VisualElement Element;

        /// <summary>Called on Enter/Submit. Return true if handled.</summary>
        public Func<bool> OnActivate;

        /// <summary>
        /// Called on Left/Right arrow. Parameter is direction (-1 or +1).
        /// Return true to consume the input (prevents focus movement).
        /// </summary>
        public Func<int, bool> OnHorizontal;

        /// <summary>Called when this item becomes the focused item.</summary>
        public Action OnFocused;

        /// <summary>Called when this item loses focus (another item is selected).</summary>
        public Action OnBlurred;

        /// <summary>
        /// When true, FocusNavigator won't add the kb-focusable/kb-focused classes
        /// to this element. Use for items that handle their own focus visual
        /// (e.g. sliders with an overlay ring).
        /// </summary>
        public bool CustomFocusVisual;
    }

    public enum NavDir
    {
        Up,
        Down,
        Left,
        Right,
    }

    private const string FocusableClass = "kb-focusable";
    private const string FocusClass = "kb-focused";

    private readonly VisualElement _root;
    private List<FocusItem> _items = new List<FocusItem>();
    private Dictionary<(int, NavDir), int> _navGraph = new Dictionary<(int, NavDir), int>();
    private readonly HashSet<(int, NavDir)> _navBreakpoints = new HashSet<(int, NavDir)>();
    private int _currentIndex = -1;
    private bool _ringVisible;

    /// <summary>
    /// True when the ring was revealed by a keyboard action (Tab / arrows /
    /// Submit), false when it was revealed by a pointer click. Used by
    /// <see cref="Dispose"/> to decide whether the next screen should start
    /// with the ring visible — click-revealed rings don't carry across
    /// scenes, so a fresh scene opens without a stray highlight.
    /// </summary>
    private bool _ringFromKeyboard;

    // Modal push/pop stack.
    private struct SavedState
    {
        public List<FocusItem> Items;
        public Dictionary<(int, NavDir), int> NavGraph;
        public int CurrentIndex;
        public bool RingVisible;
        public bool RingFromKeyboard;
    }

    private readonly Stack<SavedState> _modalStack = new Stack<SavedState>();
    private Func<bool> _modalCancelAction;
    private bool _cancelConsumedThisFrame;

    // DAS repeaters for directional navigation.
    private readonly DASRepeater _dasUp = new DASRepeater();
    private readonly DASRepeater _dasDown = new DASRepeater();
    private readonly DASRepeater _dasLeft = new DASRepeater();
    private readonly DASRepeater _dasRight = new DASRepeater();

    /// <summary>
    /// The currently active FocusNavigator. Set automatically by the constructor;
    /// SettingsController saves and restores it across open/close.
    /// </summary>
    public static FocusNavigator Active { get; set; }

    /// <summary>
    /// True if keyboard navigation was active when the previous navigator was disposed.
    /// New navigators can check this to start with the ring visible.
    /// </summary>
    public static bool WasKeyboardActive { get; private set; }

    /// <summary>Reset static state for test isolation. Call in test TearDown.</summary>
    public static void ResetStaticState()
    {
        Active = null;
        WasKeyboardActive = false;
    }

    /// <summary>Current focused item index. -1 if nothing is focused.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Number of items in the current navigator (excluding modal overlays).</summary>
    public int ItemCount => _items.Count;

    /// <summary>Get the VisualElement at a given item index.</summary>
    public VisualElement GetItemElement(int index)
    {
        if (index < 0 || index >= _items.Count)
            return null;
        return _items[index].Element;
    }

    /// <summary>True if the keyboard focus ring is currently visible.</summary>
    public bool IsRingVisible => _ringVisible;

    /// <summary>Fired when the focused index changes.</summary>
    public event Action<int> FocusChanged;

    public FocusNavigator(VisualElement root)
    {
        _root = root;
        Active = this;

        // Hide focus ring on any pointer interaction.
        _root.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);

        // Suppress Unity's built-in keyboard navigation so it doesn't interfere
        // with our FocusNavigator. We read InputActions directly, not these events.
        _root.RegisterCallback<NavigationMoveEvent>(SuppressNav, TrickleDown.TrickleDown);
        _root.RegisterCallback<NavigationSubmitEvent>(SuppressNav, TrickleDown.TrickleDown);
        _root.RegisterCallback<NavigationCancelEvent>(SuppressNav, TrickleDown.TrickleDown);
    }

    private static void SuppressNav<T>(T evt)
        where T : EventBase<T>, new()
    {
        evt.StopPropagation();
    }

    /// <summary>Set the list of navigable items. Resets focus to the given index.</summary>
    public void SetItems(List<FocusItem> items, int initialIndex = 0)
    {
        ClearAllClasses();
        _navGraph.Clear();
        _items = items ?? new List<FocusItem>();
        foreach (var item in _items)
        {
            if (!item.CustomFocusVisual)
                item.Element.AddToClassList(FocusableClass);
        }
        _currentIndex = _items.Count > 0 ? Mathf.Clamp(initialIndex, 0, _items.Count - 1) : -1;

        // Carry keyboard state across screen transitions.
        _ringVisible = WasKeyboardActive;
        WasKeyboardActive = false;
        if (_ringVisible)
            ApplyFocusRing();

        ResetDAS();
    }

    /// <summary>
    /// Add a directional navigation edge. When <paramref name="from"/> is focused
    /// and the user presses <paramref name="dir"/>, focus jumps to <paramref name="to"/>.
    /// </summary>
    /// <summary>Remove all navigation edges. Items and focus state are kept.</summary>
    public void ClearLinks()
    {
        _navGraph.Clear();
        _navBreakpoints.Clear();
    }

    public void Link(int from, NavDir dir, int to)
    {
        _navGraph[(from, dir)] = to;
    }

    /// <summary>
    /// Add a navigation edge that suppresses DAS after traversal,
    /// requiring a fresh key press to continue. Use at region boundaries
    /// (e.g. last entry → sort tabs).
    /// </summary>
    public void LinkBreak(int from, NavDir dir, int to)
    {
        _navGraph[(from, dir)] = to;
        _navBreakpoints.Add((from, dir));
    }

    /// <summary>Remove a single navigation edge.</summary>
    public void ClearLink(int from, NavDir dir)
    {
        _navGraph.Remove((from, dir));
    }

    /// <summary>
    /// Bidirectional link: A→B in <paramref name="dir"/>, B→A in the opposite.
    /// </summary>
    public void LinkBidi(int a, NavDir dir, int b)
    {
        _navGraph[(a, dir)] = b;
        _navGraph[(b, Opposite(dir))] = a;
    }

    /// <summary>
    /// Link a vertical chain of items with Up/Down edges.
    /// Items at indices <paramref name="start"/> through <paramref name="start"/>+<paramref name="count"/>-1.
    /// </summary>
    public void LinkChain(int start, int count)
    {
        for (int i = 0; i < count - 1; i++)
            LinkBidi(start + i, NavDir.Down, start + i + 1);
    }

    /// <summary>
    /// Link a horizontal row of items with Left/Right edges.
    /// </summary>
    public void LinkRow(int start, int count)
    {
        for (int i = 0; i < count - 1; i++)
            LinkBidi(start + i, NavDir.Right, start + i + 1);
    }

    private static NavDir Opposite(NavDir dir)
    {
        switch (dir)
        {
            case NavDir.Up:
                return NavDir.Down;
            case NavDir.Down:
                return NavDir.Up;
            case NavDir.Left:
                return NavDir.Right;
            case NavDir.Right:
                return NavDir.Left;
            default:
                return dir;
        }
    }

    // -- Modal push/pop --------------------------------------------------------

    /// <summary>
    /// Push a modal overlay onto the navigation stack. Saves the current state
    /// and replaces items/links with the modal's. Call <see cref="PopModal"/>
    /// when the modal closes.
    /// </summary>
    /// <param name="onCancel">Called when Escape is pressed while the modal is active.</param>
    public void PushModal(List<FocusItem> items, int initialIndex = 0, Func<bool> onCancel = null)
    {
        Debug.Assert(
            _modalStack.Count < 10,
            "Modal stack depth exceeded 10 — likely a push-without-pop bug"
        );

        // Save current state.
        _modalStack.Push(
            new SavedState
            {
                Items = _items,
                NavGraph = _navGraph,
                CurrentIndex = _currentIndex,
                RingVisible = _ringVisible,
                RingFromKeyboard = _ringFromKeyboard,
            }
        );

        ClearFocusRing();

        // Replace with modal state.
        _modalCancelAction = onCancel;
        _items = items ?? new List<FocusItem>();
        _navGraph = new Dictionary<(int, NavDir), int>();
        foreach (var item in _items)
            item.Element.AddToClassList(FocusableClass);
        _currentIndex = Mathf.Clamp(initialIndex, 0, Mathf.Max(0, _items.Count - 1));
        _ringVisible = true;
        _ringFromKeyboard = true;
        ResetDAS();
        ApplyFocusRing();
    }

    /// <summary>Pop the modal overlay and restore the previous navigation state.</summary>
    public void PopModal()
    {
        if (_modalStack.Count == 0)
            return;

        ClearAllClasses();
        var saved = _modalStack.Pop();
        _modalCancelAction = null;
        _items = saved.Items;
        _navGraph = saved.NavGraph;
        _currentIndex = saved.CurrentIndex;
        _ringVisible = saved.RingVisible;
        _ringFromKeyboard = saved.RingFromKeyboard;
        _prevFocusedIndex = _currentIndex; // avoid stale blur on wrong item
        foreach (var item in _items)
            item.Element.AddToClassList(FocusableClass);
        if (_ringVisible)
            ApplyFocusRing();
    }

    /// <summary>True when a modal overlay is active on this navigator.</summary>
    public bool HasModal => _modalStack.Count > 0;

    // -------------------------------------------------------------------------

    /// <summary>Convenience overload for simple element lists (no custom handlers).</summary>
    public void SetItems(List<VisualElement> elements, int initialIndex = 0)
    {
        var items = new List<FocusItem>(elements.Count);
        foreach (var el in elements)
            items.Add(new FocusItem { Element = el });
        SetItems(items, initialIndex);
    }

    /// <summary>
    /// Call from MonoBehaviour.Update(). Reads UI Navigate/Submit/Cancel/Tab actions
    /// from KeybindManager and updates focus accordingly.
    /// </summary>
    public void Update()
    {
        _cancelConsumedThisFrame = false;

        // Modal may have items even when the base navigator has none.
        if (_items.Count == 0 && _modalStack.Count == 0)
            return;

        var km = KeybindManager.Instance;
        if (km == null)
            return;

        Vector2 nav = km.Navigate.ReadValue<Vector2>();
        bool up = _dasUp.Update(nav.y > 0.5f);
        bool down = _dasDown.Update(nav.y < -0.5f);
        bool left = _dasLeft.Update(nav.x < -0.5f);
        bool right = _dasRight.Update(nav.x > 0.5f);

        bool tabPressed = km.Tab.WasPerformedThisFrame();
        bool shiftHeld =
            UnityEngine.InputSystem.Keyboard.current != null
            && UnityEngine.InputSystem.Keyboard.current.shiftKey.isPressed;

        bool anyNav = up || down || left || right || tabPressed;

        // First navigation key just reveals the ring at the current index.
        if (anyNav && !_ringVisible)
        {
            ShowRing();
            ApplyFocusRing();
            FocusChanged?.Invoke(_currentIndex);
            return;
        }

        bool moved = false;
        var cur =
            (_currentIndex >= 0 && _currentIndex < _items.Count) ? _items[_currentIndex] : default;

        // Horizontal: item handler first (sliders, dropdowns), then graph edge.
        // OnHorizontal returns true to consume the input.
        bool horizontalConsumed = false;
        if (left || right)
        {
            int dir = right ? 1 : -1;
            if (cur.OnHorizontal != null)
                horizontalConsumed = cur.OnHorizontal(dir);
        }

        // Check nav graph edges (skip horizontal if item handler consumed it).
        if (!horizontalConsumed)
        {
            NavDir? pressedDir =
                up ? NavDir.Up
                : down ? NavDir.Down
                : left ? NavDir.Left
                : right ? NavDir.Right
                : (NavDir?)null;

            if (
                pressedDir.HasValue
                && _navGraph.TryGetValue((_currentIndex, pressedDir.Value), out int graphTarget)
            )
            {
                // Breakpoint edges only fire on the initial press, not DAS repeats.
                // This stops DAS at region boundaries (e.g. entry #1 → sort tabs).
                bool blocked = false;
                if (_navBreakpoints.Contains((_currentIndex, pressedDir.Value)))
                {
                    bool isInitial =
                        (up && _dasUp.WasInitialPress)
                        || (down && _dasDown.WasInitialPress)
                        || (left && _dasLeft.WasInitialPress)
                        || (right && _dasRight.WasInitialPress);
                    blocked = !isInitial;
                }

                if (!blocked)
                {
                    _currentIndex = graphTarget;
                    moved = true;
                }
            }
        }

        // Tab cycles through items linearly.
        if (tabPressed)
        {
            if (shiftHeld)
                _currentIndex = (_currentIndex - 1 + _items.Count) % _items.Count;
            else
                _currentIndex = (_currentIndex + 1) % _items.Count;
            moved = true;
        }

        if (moved)
        {
            ApplyFocusRing();
            FocusChanged?.Invoke(_currentIndex);
        }

        // Submit activates the focused item. When a text field is focused,
        // only fire if the item has an OnActivate (e.g. form submit).
        // Items without OnActivate (e.g. EditableLabel) handle Enter themselves.
        if (km.Submit.WasPerformedThisFrame())
        {
            if (!_ringVisible)
            {
                // Enter without visible ring: show the ring (same as first nav key).
                ShowRing();
                ApplyFocusRing();
            }
            else if (!km.TextFieldFocused)
            {
                ActivateCurrent();
            }
            else if (
                _currentIndex >= 0
                && _currentIndex < _items.Count
                && _items[_currentIndex].OnActivate != null
            )
            {
                _items[_currentIndex].OnActivate();
            }
        }

        // Escape cancels the modal if one is active.
        if (
            _modalStack.Count > 0
            && _modalCancelAction != null
            && km.Cancel.WasPerformedThisFrame()
        )
        {
            _cancelConsumedThisFrame = true;
            _modalCancelAction();
        }
    }

    /// <summary>
    /// True when a modal consumed Cancel this frame, or a modal is still active.
    /// Screen controllers should skip their own Cancel handling when this is true.
    /// </summary>
    public bool ConsumesCancel => _cancelConsumedThisFrame || _modalStack.Count > 0;

    /// <summary>Force-set focus to a specific index and show the ring.</summary>
    public void SetFocus(int index)
    {
        if (index < 0 || index >= _items.Count)
            return;
        _currentIndex = index;
        ShowRing();
        ApplyFocusRing();
        FocusChanged?.Invoke(_currentIndex);
    }

    /// <summary>Activate the currently focused item. Calls OnActivate if set.</summary>
    public void ActivateCurrent()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;

        var item = _items[_currentIndex];
        if (item.OnActivate != null)
            item.OnActivate();
    }

    /// <summary>Hide the focus ring without clearing the index.</summary>
    public void HideRing()
    {
        _ringVisible = false;
        ClearFocusRing();
    }

    /// <summary>Show the focus ring at the current index (keyboard-sourced).</summary>
    public void ShowRing()
    {
        _ringVisible = true;
        _ringFromKeyboard = true;
    }

    /// <summary>Remove the kb-focused class from all items.</summary>
    public void ClearFocusRing()
    {
        foreach (var item in _items)
            item.Element.RemoveFromClassList(FocusClass);
    }

    /// <summary>Remove both kb-focusable and kb-focused from all items.</summary>
    private void ClearAllClasses()
    {
        foreach (var item in _items)
        {
            if (!item.CustomFocusVisual)
                item.Element.RemoveFromClassList(FocusableClass);
            item.Element.RemoveFromClassList(FocusClass);
        }
    }

    /// <summary>Scroll the focused element into view if inside a ScrollView.</summary>
    /// <summary>
    /// Scroll the focused element into view, biased toward the center of the
    /// viewport rather than the edge. Provides comfortable spacing around the
    /// focused item.
    /// </summary>
    public void ScrollToFocused()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count)
            return;
        var el = _items[_currentIndex].Element;
        var scroll = FindParentScrollView(el);
        if (scroll == null)
            return;

        // Get element position relative to the scroll content.
        float elTop = el.layout.y;
        var parent = el.parent;
        while (parent != null && parent != scroll.contentContainer)
        {
            elTop += parent.layout.y;
            parent = parent.parent;
        }
        float elBottom = elTop + el.layout.height;

        float viewHeight = scroll.contentViewport.layout.height;
        float scrollPos = scroll.verticalScroller.value;
        float viewTop = scrollPos;
        float viewBottom = scrollPos + viewHeight;

        // Don't scroll if all content fits in the viewport.
        float maxScroll = scroll.verticalScroller.highValue;
        if (maxScroll <= 0)
            return;

        // Scroll to center the element in the viewport.
        float elCenter = elTop + el.layout.height * 0.5f;
        float targetScroll = elCenter - viewHeight * 0.5f;
        targetScroll = Mathf.Clamp(targetScroll, 0, maxScroll);

        // Only scroll if the element is outside the center 40% of the viewport.
        float margin = viewHeight * 0.3f;
        if (elTop < viewTop + margin || elBottom > viewBottom - margin)
            scroll.verticalScroller.value = targetScroll;
    }

    public void Dispose()
    {
        if (Active == this)
        {
            // Only carry keyboard-sourced rings into the next scene. Click-
            // sourced rings stay confined to their own scene so a fresh
            // screen doesn't open with a stray highlight the user didn't
            // ask for.
            WasKeyboardActive = _ringVisible && _ringFromKeyboard;
            Active = null;
        }
        ClearAllClasses();
        _root.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
        _root.UnregisterCallback<NavigationMoveEvent>(SuppressNav, TrickleDown.TrickleDown);
        _root.UnregisterCallback<NavigationSubmitEvent>(SuppressNav, TrickleDown.TrickleDown);
        _root.UnregisterCallback<NavigationCancelEvent>(SuppressNav, TrickleDown.TrickleDown);
    }

    // -- Private helpers ------------------------------------------------------

    private int _prevFocusedIndex = -1;

    private void ApplyFocusRing()
    {
        ClearFocusRing();

        // Fire blur/focus callbacks on transition.
        if (_prevFocusedIndex != _currentIndex)
        {
            if (_prevFocusedIndex >= 0 && _prevFocusedIndex < _items.Count)
                _items[_prevFocusedIndex].OnBlurred?.Invoke();
            if (_ringVisible && _currentIndex >= 0 && _currentIndex < _items.Count)
                _items[_currentIndex].OnFocused?.Invoke();
            _prevFocusedIndex = _currentIndex;
        }

        if (_ringVisible && _currentIndex >= 0 && _currentIndex < _items.Count)
        {
            _items[_currentIndex].Element.AddToClassList(FocusClass);
            ScrollToFocused();
        }
    }

    private void OnPointerDown(PointerDownEvent evt)
    {
        // Locate which navigator item (if any) owns the click target — walk
        // up the ancestors so clicks on inner children (a Label inside a
        // TextField's input) still resolve to the owning focusable.
        int clickedIndex = -1;
        var target = evt.target as VisualElement;
        while (target != null && clickedIndex < 0)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].Element == target)
                {
                    clickedIndex = i;
                    break;
                }
            }
            target = target.parent;
        }

        if (clickedIndex >= 0)
        {
            // Treat the click as a focus selection so subsequent Tab/arrow
            // presses behave identically to the keyboard flow — no divergence
            // between pointer and keyboard modes. Ring becomes visible at the
            // clicked item, but marked as pointer-sourced so it doesn't carry
            // across scene transitions (we don't want a stray highlight on a
            // fresh screen just because the user clicked on the previous one).
            _currentIndex = clickedIndex;
            _ringVisible = true;
            _ringFromKeyboard = false;
            ApplyFocusRing();
            FocusChanged?.Invoke(_currentIndex);
        }
        else if (_ringVisible)
        {
            // Click outside any focusable item: hide the ring (the user is
            // interacting with something not in our list).
            _ringVisible = false;
            _ringFromKeyboard = false;
            ClearFocusRing();
        }
    }

    /// <summary>
    /// Suppress all DAS repeaters so held keys don't fire as new presses.
    /// Use after a sub-component (dropdown, modal) closes.
    /// </summary>
    public void SuppressDAS()
    {
        _dasUp.Suppress();
        _dasDown.Suppress();
        _dasLeft.Suppress();
        _dasRight.Suppress();
    }

    public void ResetDAS()
    {
        _dasUp.Reset();
        _dasDown.Reset();
        _dasLeft.Reset();
        _dasRight.Reset();
    }

    private static ScrollView FindParentScrollView(VisualElement el)
    {
        var parent = el.parent;
        while (parent != null)
        {
            if (parent is ScrollView sv)
                return sv;
            parent = parent.parent;
        }
        return null;
    }
}
