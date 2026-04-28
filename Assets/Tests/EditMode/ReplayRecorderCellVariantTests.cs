using NUnit.Framework;

/// <summary>
/// Coverage for the optional sim-time parameter on tap-event recorder
/// methods. Endless mode passes simTime so the verifier can drive its
/// time-based loop deterministically; classic / coop omit it (wall-clock
/// timestamp suffices).
/// </summary>
[TestFixture]
public class ReplayRecorderCellVariantTests
{
    [Test]
    public void RecordClear_WithSimTime_PopulatesSimTimeAndPos()
    {
        var r = new ReplayRecorder();
        r.RecordClear(posX: 3f, posY: 7f, simTime: 1.5f);

        var e = r.Events[0];
        Assert.That(e.type, Is.EqualTo(ReplayEventType.Clear));
        Assert.That(e.simTime, Is.EqualTo(1.5f));
        Assert.That(e.posX, Is.EqualTo(3f));
        Assert.That(e.posY, Is.EqualTo(7f));
        Assert.That(e.timestamp, Is.Not.Null.Or.Empty, "wall-clock kept as debug aid");
    }

    [Test]
    public void RecordReject_WithSimTime_TypeIsReject()
    {
        var r = new ReplayRecorder();
        r.RecordReject(5f, 9f, simTime: 2.0f);
        Assert.That(r.Events[0].type, Is.EqualTo(ReplayEventType.Reject));
        Assert.That(r.Events[0].simTime, Is.EqualTo(2.0f));
    }

    [Test]
    public void RecordMiss_WithSimTime_TypeIsMiss()
    {
        var r = new ReplayRecorder();
        r.RecordMiss(0f, 0f, simTime: 2.5f);
        Assert.That(r.Events[0].type, Is.EqualTo(ReplayEventType.Miss));
        Assert.That(r.Events[0].simTime, Is.EqualTo(2.5f));
    }

    [Test]
    public void RecordTopout_TypeIsTopout_NoSpatialFields()
    {
        var r = new ReplayRecorder();
        r.RecordTopout(simTime: 42.0f);
        var e = r.Events[0];
        Assert.That(e.type, Is.EqualTo(ReplayEventType.Topout));
        Assert.That(e.simTime, Is.EqualTo(42.0f));
        Assert.That(e.posX, Is.Null);
        Assert.That(e.posY, Is.Null);
    }

    [Test]
    public void RecordClear_WithoutSimTime_LeavesSimTimeNull()
    {
        var r = new ReplayRecorder();
        r.RecordClear(1.5f, 2.5f); // classic-style: no simTime
        var e = r.Events[0];
        Assert.That(e.simTime, Is.Null, "classic recordings shouldn't synthesize a simTime");
        Assert.That(e.posX, Is.EqualTo(1.5f));
        Assert.That(e.posY, Is.EqualTo(2.5f));
    }

    [Test]
    public void Mixed_Recordings_Coexist_With_Monotonic_Seq()
    {
        var r = new ReplayRecorder();
        r.RecordSessionStart(); // seq 0
        r.RecordClear(1.5f, 2.5f); // seq 1, classic-style
        r.RecordClear(3f, 4f, simTime: 1.0f); // seq 2, endless-style
        r.RecordTopout(2.0f); // seq 3

        Assert.That(r.Events.Count, Is.EqualTo(4));
        for (int i = 0; i < r.Events.Count; i++)
            Assert.That(r.Events[i].seq, Is.EqualTo(i));
    }
}
