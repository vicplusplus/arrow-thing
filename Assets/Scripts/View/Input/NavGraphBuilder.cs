using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

/// <summary>
/// Resolves the named slots in a <see cref="NavGraph"/> to live UI elements
/// and applies the graph to a <see cref="FocusNavigator"/>. See
/// <see cref="NavGraph"/> for the inspector model and a usage example.
///
/// Slots are bound by name in any order; <see cref="Apply"/> emits FocusItems
/// in the order the slots appear in the SO. Slots with a null element are
/// skipped, and any link touching an unbound slot is dropped — so a single SO
/// can describe a graph that varies across platforms.
/// </summary>
public sealed class NavGraphBuilder
{
    private readonly NavGraph _graph;
    private readonly Dictionary<string, FocusNavigator.FocusItem> _bindings = new Dictionary<
        string,
        FocusNavigator.FocusItem
    >(StringComparer.Ordinal);
    private readonly HashSet<string> _slotNames;

    public NavGraphBuilder(NavGraph graph)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        _graph = graph;
        _slotNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in _graph.slots)
            _slotNames.Add(slot.name);
    }

    /// <summary>
    /// Bind a slot to a UI element with optional callbacks. Passing
    /// <paramref name="element"/> = null is allowed and skips the slot
    /// (intended for platform-conditional items like Quit on WebGL).
    /// </summary>
    public NavGraphBuilder Bind(
        string slotName,
        VisualElement element,
        Func<bool> onActivate = null,
        Func<int, bool> onHorizontal = null,
        Action onFocused = null,
        Action onBlurred = null,
        bool customFocusVisual = false
    )
    {
        if (slotName == null)
            throw new ArgumentNullException(nameof(slotName));
        if (!_slotNames.Contains(slotName))
            throw new ArgumentException(
                $"NavGraph '{_graph.name}' has no slot named '{slotName}'.",
                nameof(slotName)
            );
        if (element == null)
            return this;

        _bindings[slotName] = new FocusNavigator.FocusItem
        {
            Element = element,
            OnActivate = onActivate,
            OnHorizontal = onHorizontal,
            OnFocused = onFocused,
            OnBlurred = onBlurred,
            CustomFocusVisual = customFocusVisual,
        };
        return this;
    }

    /// <summary>
    /// Bind a slot to a fully-constructed <see cref="FocusNavigator.FocusItem"/>.
    /// Useful when the controller has a factory (text-field, slider, dropdown, etc.)
    /// that already produces a complete <c>FocusItem</c>. An item with a null
    /// <see cref="FocusNavigator.FocusItem.Element"/> is treated the same as an
    /// unbound slot.
    /// </summary>
    public NavGraphBuilder BindItem(string slotName, FocusNavigator.FocusItem item)
    {
        if (slotName == null)
            throw new ArgumentNullException(nameof(slotName));
        if (!_slotNames.Contains(slotName))
            throw new ArgumentException(
                $"NavGraph '{_graph.name}' has no slot named '{slotName}'.",
                nameof(slotName)
            );
        if (item.Element == null)
            return this;

        _bindings[slotName] = item;
        return this;
    }

    /// <summary>
    /// Apply the resolved graph to <paramref name="nav"/>. Items are emitted
    /// in slot-declaration order, skipping unbound slots. Initial focus
    /// resolves to <paramref name="initialFocus"/> if given, else the SO's
    /// <see cref="NavGraph.initialFocus"/>, else the first bound slot.
    /// </summary>
    public void Apply(FocusNavigator nav, string initialFocus = null)
    {
        if (nav == null)
            throw new ArgumentNullException(nameof(nav));

        var items = new List<FocusNavigator.FocusItem>(_bindings.Count);
        var indexBySlot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var slot in _graph.slots)
        {
            if (!_bindings.TryGetValue(slot.name, out var item))
                continue;
            indexBySlot[slot.name] = items.Count;
            items.Add(item);
        }

        var focusName = !string.IsNullOrEmpty(initialFocus) ? initialFocus : _graph.initialFocus;
        int initialIndex = 0;
        if (!string.IsNullOrEmpty(focusName) && indexBySlot.TryGetValue(focusName, out var fi))
            initialIndex = fi;

        nav.SetItems(items, initialIndex);

        foreach (var link in _graph.links)
        {
            if (
                !indexBySlot.TryGetValue(link.from, out var fromIdx)
                || !indexBySlot.TryGetValue(link.to, out var toIdx)
            )
                continue;

            if (link.bidirectional)
                nav.LinkBidi(fromIdx, link.dir, toIdx);
            else if (link.breakDAS)
                nav.LinkBreak(fromIdx, link.dir, toIdx);
            else
                nav.Link(fromIdx, link.dir, toIdx);
        }
    }

    /// <summary>True iff a slot with this name has been bound.</summary>
    public bool IsBound(string slotName) => _bindings.ContainsKey(slotName);
}
