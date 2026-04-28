using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Declarative, inspector-edited description of a focus-navigation graph for a
/// single screen or panel. Pairs with <see cref="NavGraphBuilder"/>, which
/// resolves slot names to <see cref="UnityEngine.UIElements.VisualElement"/>s
/// at runtime and applies the links to a <see cref="FocusNavigator"/>.
///
/// Typical use from a controller:
/// <code>
/// var builder = new NavGraphBuilder(rootNavGraph);
/// builder.Bind("Play", root.Q&lt;Button&gt;("play-btn"), onActivate: () =&gt; { ...; return true; });
/// builder.Bind("Settings", root.Q&lt;Button&gt;("settings-btn"));
/// builder.Apply(navigator);
/// </code>
///
/// Slots whose elements are not bound (e.g. Quit on WebGL) are silently
/// skipped, and any link that touches an unbound slot is dropped — so a
/// single SO can describe a graph for multiple platform variants.
/// </summary>
[CreateAssetMenu(fileName = "NavGraph", menuName = "Arrow Thing/Nav Graph")]
public sealed class NavGraph : ScriptableObject
{
    [Serializable]
    public struct Slot
    {
        [Tooltip("Identifier used by NavGraphBuilder.Bind(name, element).")]
        public string name;
    }

    [Serializable]
    public struct Link
    {
        [Tooltip("Source slot name (must appear in slots).")]
        public string from;

        [Tooltip("Direction the user presses to traverse this edge.")]
        public FocusNavigator.NavDir dir;

        [Tooltip("Destination slot name (must appear in slots).")]
        public string to;

        [Tooltip(
            "If true, also adds the reverse edge (to → opposite(dir) → from). "
                + "Equivalent to FocusNavigator.LinkBidi."
        )]
        public bool bidirectional;

        [Tooltip(
            "If true, suppresses DAS after traversing this edge — the next move "
                + "requires a fresh key press. Use at region boundaries. "
                + "Equivalent to FocusNavigator.LinkBreak. Ignored when bidirectional is true."
        )]
        public bool breakDAS;
    }

    [Tooltip(
        "All navigable slots in declaration order. Order determines the FocusItem "
            + "index assigned to each slot, but consumers should reference slots by name "
            + "and not rely on the index."
    )]
    public List<Slot> slots = new List<Slot>();

    [Tooltip("Edges between slots. See Link tooltips for direction semantics.")]
    public List<Link> links = new List<Link>();

    [Tooltip(
        "Slot to focus when this graph is first applied. Empty → falls back to "
            + "the first bound slot. Overridable per-call via NavGraphBuilder.Apply(initialFocus)."
    )]
    public string initialFocus;
}
