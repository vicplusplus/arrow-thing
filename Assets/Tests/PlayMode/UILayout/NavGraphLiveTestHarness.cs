using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives a <see cref="NavigableScene"/> through its real <c>OnEnable</c>
/// lifecycle in a PlayMode test, then exposes the resulting live
/// <see cref="FocusNavigator"/> so tests can read what was actually wired
/// up — instead of comparing the test author's claim against UXML
/// visibility (the gap that <see cref="NavigationCoverageTests"/> couldn't
/// see, which let the leaderboard's keyboard-unreachable mode tabs ship in
/// PR #154).
///
/// <para><b>How it works</b>: the helper attaches the controller to a fresh
/// inactive <see cref="GameObject"/>, sets the (private) <c>uiDocument</c>
/// SerializeField via reflection so the controller can find its panel
/// without needing a real scene, then activates the object — which fires
/// <c>OnEnable</c>, runs the controller's <c>BuildUI</c> + <c>BuildNavGraph</c>
/// against the loaded UXML root, and produces the FocusNavigator the live
/// game would have produced.</para>
///
/// <para><b>What it does NOT do</b>: invoke any controller-specific test
/// shim. Generic by design — the user explicitly rejected the per-screen
/// approach because a per-screen surface still trusts the implementer to
/// wire it up.</para>
///
/// <para>If a controller's <c>OnEnable</c> throws or has side effects that
/// the test environment can't support (e.g. requires a server connection),
/// the wrapping test should bail with a clear message rather than this
/// helper trying to mask the failure.</para>
/// </summary>
public static class NavGraphLiveTestHarness
{
    /// <summary>
    /// Adds <typeparamref name="T"/> to a child of <paramref name="host"/>,
    /// wires its <c>uiDocument</c> SerializeField to <paramref name="hostDocument"/>,
    /// activates the child (firing OnEnable → BuildUI → BuildNavGraph), and
    /// returns the resulting active FocusNavigator + controller instance.
    /// Caller is responsible for any pre-OnEnable state setup that affects
    /// nav-graph wiring (e.g. selecting endless mode before checking the
    /// endless nav state).
    /// </summary>
    public static (FocusNavigator nav, T controller) WireController<T>(
        GameObject host,
        UIDocument hostDocument
    )
        where T : NavigableScene
    {
        var controllerObj = new GameObject(typeof(T).Name + "Test");
        controllerObj.transform.SetParent(host.transform);
        controllerObj.SetActive(false);

        var controller = controllerObj.AddComponent<T>();

        // The uiDocument field is on NavigableScene (private + SerializeField).
        // BindingFlags.NonPublic|Instance reaches it; FlattenHierarchy isn't
        // needed because GetField walks declared fields on the type and
        // NavigableScene is in the inheritance chain, but to be safe we
        // search up the chain explicitly.
        var fld = FindField(typeof(T), "uiDocument");
        if (fld == null)
            throw new System.InvalidOperationException(
                "uiDocument field not found on "
                    + typeof(T).Name
                    + " or its bases — is it still declared on NavigableScene?"
            );
        fld.SetValue(controller, hostDocument);

        // Activating fires Awake then OnEnable. NavigableScene.OnEnable
        // creates the FocusNavigator and calls BuildNavGraph; FocusNavigator's
        // constructor sets itself as the static Active.
        controllerObj.SetActive(true);

        return (FocusNavigator.Active, controller);
    }

    /// <summary>
    /// Snapshots the current FocusNavigator's items as a list of UXML
    /// element names. Items whose element is null or unnamed are returned
    /// as the empty string so a missing name surfaces in test failures
    /// rather than silently filtering out.
    /// </summary>
    public static List<string> GetNavigableElementNames(FocusNavigator nav)
    {
        var names = new List<string>(nav.ItemCount);
        for (int i = 0; i < nav.ItemCount; i++)
        {
            var el = nav.GetItemElement(i);
            names.Add(el != null ? el.name ?? string.Empty : string.Empty);
        }
        return names;
    }

    private static FieldInfo FindField(System.Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
                return f;
        }
        return null;
    }
}
