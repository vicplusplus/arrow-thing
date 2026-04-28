using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Tests for the <see cref="NavGraph"/> ScriptableObject + <see cref="NavGraphBuilder"/>
/// pair. Covers slot resolution, conditional binding (unbound slots silently
/// drop themselves and any link that touches them), initial-focus fallback
/// chain, and the three link kinds (one-way, bidi, DAS-break).
///
/// Builds a real <see cref="FocusNavigator"/> bound to a real <see cref="VisualElement"/>
/// — UI Toolkit elements are pure C# and run fine in EditMode.
/// </summary>
[TestFixture]
public class NavGraphBuilderTests
{
    private VisualElement _root;
    private FocusNavigator _nav;

    [SetUp]
    public void SetUp()
    {
        _root = new VisualElement();
        _nav = new FocusNavigator(_root);
    }

    [TearDown]
    public void TearDown()
    {
        FocusNavigator.ResetStaticState();
    }

    private static NavGraph MakeGraph(
        List<NavGraph.Slot> slots,
        List<NavGraph.Link> links,
        string initialFocus = null
    )
    {
        var g = ScriptableObject.CreateInstance<NavGraph>();
        g.slots = slots;
        g.links = links;
        g.initialFocus = initialFocus;
        return g;
    }

    private static Button NewButton(string name)
    {
        var b = new Button { name = name };
        return b;
    }

    // ── Bind / Apply basics ────────────────────────────────────────────

    [Test]
    public void Apply_SetsItemsInSlotDeclarationOrder()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
                new NavGraph.Slot { name = "C" },
            },
            links: new List<NavGraph.Link>()
        );

        var a = NewButton("a");
        var b = NewButton("b");
        var c = NewButton("c");
        _root.Add(a);
        _root.Add(b);
        _root.Add(c);

        new NavGraphBuilder(graph)
            // Bind out of order to verify slot order, not bind order, decides indexing.
            .Bind("C", c)
            .Bind("A", a)
            .Bind("B", b)
            .Apply(_nav);

        Assert.AreEqual(3, _nav.ItemCount);
        Assert.AreSame(a, _nav.GetItemElement(0));
        Assert.AreSame(b, _nav.GetItemElement(1));
        Assert.AreSame(c, _nav.GetItemElement(2));
    }

    [Test]
    public void Bind_NullElement_SkipsSlotAndAnyLinkTouchingIt()
    {
        // Models the WebGL-quit case: Quit slot exists in the SO but is unbound
        // on this platform. Links involving Quit should silently drop, but the
        // remaining graph (Play ↔ Settings) must still wire up cleanly.
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "Quit" },
                new NavGraph.Slot { name = "Play" },
                new NavGraph.Slot { name = "Settings" },
            },
            links: new List<NavGraph.Link>
            {
                new NavGraph.Link
                {
                    from = "Quit",
                    dir = FocusNavigator.NavDir.Down,
                    to = "Play",
                },
                new NavGraph.Link
                {
                    from = "Play",
                    dir = FocusNavigator.NavDir.Up,
                    to = "Quit",
                },
                new NavGraph.Link
                {
                    from = "Play",
                    dir = FocusNavigator.NavDir.Down,
                    to = "Settings",
                    bidirectional = true,
                },
            }
        );

        var play = NewButton("play");
        var settings = NewButton("settings");
        _root.Add(play);
        _root.Add(settings);

        new NavGraphBuilder(graph)
            .Bind("Quit", null) // unbound — explicit null is allowed
            .Bind("Play", play)
            .Bind("Settings", settings)
            .Apply(_nav);

        Assert.AreEqual(2, _nav.ItemCount, "Quit slot should not occupy an index.");
        Assert.AreSame(play, _nav.GetItemElement(0));
        Assert.AreSame(settings, _nav.GetItemElement(1));

        // Quit-touching links dropped.
        Assert.IsFalse(
            _nav.TryGetLink(0, FocusNavigator.NavDir.Up, out _),
            "Play→Up→Quit should not be applied because Quit is unbound."
        );

        // Play↔Settings still wired bidirectionally.
        Assert.IsTrue(_nav.TryGetLink(0, FocusNavigator.NavDir.Down, out var down));
        Assert.AreEqual(1, down);
        Assert.IsTrue(_nav.TryGetLink(1, FocusNavigator.NavDir.Up, out var up));
        Assert.AreEqual(0, up);
    }

    [Test]
    public void Bind_UnknownSlotName_Throws()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot> { new NavGraph.Slot { name = "Real" } },
            links: new List<NavGraph.Link>()
        );

        Assert.Throws<System.ArgumentException>(() =>
            new NavGraphBuilder(graph).Bind("Imaginary", NewButton("x"))
        );
    }

    // ── Initial focus resolution ───────────────────────────────────────

    [Test]
    public void Apply_InitialFocus_PrefersExplicitArgOverSO()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
                new NavGraph.Slot { name = "C" },
            },
            links: new List<NavGraph.Link>(),
            initialFocus: "A"
        );

        var a = NewButton("a");
        var b = NewButton("b");
        var c = NewButton("c");
        _root.Add(a);
        _root.Add(b);
        _root.Add(c);

        new NavGraphBuilder(graph)
            .Bind("A", a)
            .Bind("B", b)
            .Bind("C", c)
            .Apply(_nav, initialFocus: "C");

        Assert.AreEqual(2, _nav.CurrentIndex);
    }

    [Test]
    public void Apply_InitialFocus_FallsBackToSOInitialFocus()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
            },
            links: new List<NavGraph.Link>(),
            initialFocus: "B"
        );

        var a = NewButton("a");
        var b = NewButton("b");
        _root.Add(a);
        _root.Add(b);

        new NavGraphBuilder(graph).Bind("A", a).Bind("B", b).Apply(_nav);

        Assert.AreEqual(1, _nav.CurrentIndex);
    }

    [Test]
    public void Apply_InitialFocus_FallsBackToFirstBoundWhenUnset()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
            },
            links: new List<NavGraph.Link>()
        );

        var a = NewButton("a");
        var b = NewButton("b");
        _root.Add(a);
        _root.Add(b);

        new NavGraphBuilder(graph).Bind("A", a).Bind("B", b).Apply(_nav);

        Assert.AreEqual(0, _nav.CurrentIndex);
    }

    // ── Link semantics ────────────────────────────────────────────────

    [Test]
    public void Apply_Bidirectional_AddsBothEdges()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
            },
            links: new List<NavGraph.Link>
            {
                new NavGraph.Link
                {
                    from = "A",
                    dir = FocusNavigator.NavDir.Right,
                    to = "B",
                    bidirectional = true,
                },
            }
        );

        var a = NewButton("a");
        var b = NewButton("b");
        _root.Add(a);
        _root.Add(b);

        new NavGraphBuilder(graph).Bind("A", a).Bind("B", b).Apply(_nav);

        Assert.IsTrue(_nav.TryGetLink(0, FocusNavigator.NavDir.Right, out var ab));
        Assert.AreEqual(1, ab);
        Assert.IsTrue(_nav.TryGetLink(1, FocusNavigator.NavDir.Left, out var ba));
        Assert.AreEqual(0, ba);
    }

    [Test]
    public void Apply_BreakDAS_RegistersBreakpoint()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
            },
            links: new List<NavGraph.Link>
            {
                new NavGraph.Link
                {
                    from = "A",
                    dir = FocusNavigator.NavDir.Down,
                    to = "B",
                    breakDAS = true,
                },
            }
        );

        var a = NewButton("a");
        var b = NewButton("b");
        _root.Add(a);
        _root.Add(b);

        new NavGraphBuilder(graph).Bind("A", a).Bind("B", b).Apply(_nav);

        Assert.IsTrue(_nav.TryGetLink(0, FocusNavigator.NavDir.Down, out var to));
        Assert.AreEqual(1, to);
        Assert.IsTrue(
            _nav.IsLinkBreak(0, FocusNavigator.NavDir.Down),
            "breakDAS=true links must register as DAS breakpoints."
        );
    }

    // ── Builder state ─────────────────────────────────────────────────

    [Test]
    public void IsBound_ReportsBindingStatus()
    {
        var graph = MakeGraph(
            slots: new List<NavGraph.Slot>
            {
                new NavGraph.Slot { name = "A" },
                new NavGraph.Slot { name = "B" },
            },
            links: new List<NavGraph.Link>()
        );

        var a = NewButton("a");
        _root.Add(a);

        var builder = new NavGraphBuilder(graph).Bind("A", a).Bind("B", null);

        Assert.IsTrue(builder.IsBound("A"));
        Assert.IsFalse(builder.IsBound("B"), "Null-element binds should not register.");
        Assert.IsFalse(builder.IsBound("Missing"));
    }
}
