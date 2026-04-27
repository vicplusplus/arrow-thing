using NUnit.Framework;

[TestFixture]
public class EndlessReplayRecorderTests
{
    [Test]
    public void RecordTap_AppendsEventInOrder()
    {
        var r = new EndlessReplayRecorder();
        r.RecordTap(1.0f, 3, 4, EndlessTapKind.Cleared);
        r.RecordTap(2.5f, 0, 0, EndlessTapKind.Missed);
        r.RecordTap(3.1f, 7, 2, EndlessTapKind.Blocked);

        Assert.That(r.Events.Count, Is.EqualTo(3));
        Assert.That(r.Events[0].simTime, Is.EqualTo(1.0f));
        Assert.That(r.Events[0].cellX, Is.EqualTo(3));
        Assert.That(r.Events[0].cellY, Is.EqualTo(4));
        Assert.That(r.Events[0].tapKind, Is.EqualTo(EndlessTapKind.Cleared));
        Assert.That(r.Events[2].tapKind, Is.EqualTo(EndlessTapKind.Blocked));
    }

    [Test]
    public void RecordTopout_IsIdempotent()
    {
        var r = new EndlessReplayRecorder();
        r.RecordTap(1.0f, 0, 0, EndlessTapKind.Cleared);
        r.RecordTopout(2.0f);
        r.RecordTopout(3.0f); // ignored

        Assert.That(r.ToppedOut, Is.True);
        Assert.That(r.Events.Count, Is.EqualTo(2)); // tap + first topout only
        Assert.That(r.Events[1].type, Is.EqualTo(EndlessReplayEventType.Topout));
        Assert.That(r.Events[1].simTime, Is.EqualTo(2.0f));
    }

    [Test]
    public void Events_AreMonotonicWhenRecordedInOrder()
    {
        var r = new EndlessReplayRecorder();
        for (int i = 0; i < 10; i++)
            r.RecordTap(i * 0.5f, i, 0, EndlessTapKind.Cleared);
        r.RecordTopout(5.0f);

        for (int i = 1; i < r.Events.Count; i++)
            Assert.That(
                r.Events[i].simTime,
                Is.GreaterThanOrEqualTo(r.Events[i - 1].simTime),
                $"Event {i} sim time regressed"
            );
    }
}
