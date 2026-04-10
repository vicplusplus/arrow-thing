using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

[TestFixture]
public class VictoryLayoutTests : UILayoutTestBase
{
    private const string ShortMessage = "Nice!";
    private const string MediumMessage = "Where did my arrows go????? :(";
    private const string LongMessage =
        "DONT PRESS THAT BUTTON DOWN THERE ITS A TRAP IT WILL MAKE YOU PLAY A DIFFERENT "
        + "LEVEL ENTIRELY ITS DANGEROUS DONT DO IT NO!!!!!!!!!";

    [UnityTest]
    public IEnumerator VictoryShort_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        yield return RunVictoryTest(ratio, ShortMessage, 40, "VictoryShort");
    }

    [UnityTest]
    public IEnumerator VictoryMedium_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        yield return RunVictoryTest(ratio, MediumMessage, 28, "VictoryMedium");
    }

    [UnityTest]
    public IEnumerator VictoryLong_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        yield return RunVictoryTest(ratio, LongMessage, 20, "VictoryLong");
    }

    [UnityTest]
    public IEnumerator VictoryWithTime_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(VictoryUxmlPath, ratio);

        var overlay = root.Q("victory-overlay");
        overlay.RemoveFromClassList("victory--hidden");

        var msgLabel = root.Q<Label>("victory-message");
        msgLabel.text = ShortMessage;
        msgLabel.style.fontSize = 40;

        var timeLabel = root.Q<Label>("victory-time");
        timeLabel.text = "1:23.456";

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"VictoryWithTime @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            overlay,
            panelBounds,
            ctx,
            warn,
            msgLabel,
            timeLabel,
            root.Q<Button>("play-again-btn"),
            root.Q<Button>("menu-btn")
        );
    }

    [UnityTest]
    public IEnumerator Victory_GoldTimerAndLeaderboard_AllElementsVisible(
        [ValueSource(typeof(UILayoutTestHelper), nameof(UILayoutTestHelper.StandardAspectRatios))]
            UILayoutTestHelper.AspectRatio ratio
    )
    {
        var root = SetUpDocument(VictoryUxmlPath, ratio);

        var overlay = root.Q("victory-overlay");
        overlay.RemoveFromClassList("victory--hidden");

        var msgLabel = root.Q<Label>("victory-message");
        msgLabel.text = ShortMessage;
        msgLabel.style.fontSize = 40;

        var timeLabel = root.Q<Label>("victory-time");
        timeLabel.text = "1:23.456";
        timeLabel.AddToClassList("victory-time--gold");

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"Victory_GoldTimer @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            overlay,
            panelBounds,
            ctx,
            warn,
            msgLabel,
            timeLabel,
            root.Q<Button>("view-leaderboard-btn"),
            root.Q<Button>("play-again-btn"),
            root.Q<Button>("menu-btn")
        );
    }

    private IEnumerator RunVictoryTest(
        UILayoutTestHelper.AspectRatio ratio,
        string message,
        int fontSize,
        string label
    )
    {
        var root = SetUpDocument(VictoryUxmlPath, ratio);

        var overlay = root.Q("victory-overlay");
        overlay.RemoveFromClassList("victory--hidden");

        var msgLabel = root.Q<Label>("victory-message");
        msgLabel.text = message;
        msgLabel.style.fontSize = fontSize;

        yield return UILayoutTestHelper.WaitForLayoutResolve();

        var panelBounds = root.worldBound;
        string ctx = $"{label} @ {ratio.Name}";
        bool warn = IsKnownIssueRatio(ratio);

        AssertElements(
            overlay,
            panelBounds,
            ctx,
            warn,
            msgLabel,
            root.Q<Button>("play-again-btn"),
            root.Q<Button>("menu-btn")
        );
    }
}
