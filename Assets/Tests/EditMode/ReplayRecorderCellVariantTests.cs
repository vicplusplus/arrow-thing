using NUnit.Framework;

/// <summary>
/// Coverage for the cell + sim-time variant recorder methods used by
/// endless mode. Mirrors the existing world-pos variants but writes to
/// the simTime / cellX / cellY fields.
/// </summary>
[TestFixture]
public class ReplayRecorderCellVariantTests
{
    [Test]
    public void RecordClearAtCell_PopulatesSimTimeAndCellCoords()
    {
        var r = new ReplayRecorder();
        r.RecordClearAtCell(simTime: 1.5f, cellX: 3, cellY: 7);

        var e = r.Events[0];
        Assert.That(e.type, Is.EqualTo(ReplayEventType.Clear));
        Assert.That(e.simTime, Is.EqualTo(1.5f));
        Assert.That(e.cellX, Is.EqualTo(3));
        Assert.That(e.cellY, Is.EqualTo(7));
        Assert.That(e.posX, Is.Null, "world-pos fields stay null on cell variant");
        Assert.That(e.posY, Is.Null);
        Assert.That(e.timestamp, Is.Not.Null.Or.Empty, "wall-clock kept as debug aid");
    }

    [Test]
    public void RecordRejectAtCell_TypeIsReject()
    {
        var r = new ReplayRecorder();
        r.RecordRejectAtCell(2.0f, 5, 9);
        Assert.That(r.Events[0].type, Is.EqualTo(ReplayEventType.Reject));
    }

    [Test]
    public void RecordMissAtCell_TypeIsMiss()
    {
        var r = new ReplayRecorder();
        r.RecordMissAtCell(2.5f, 0, 0);
        Assert.That(r.Events[0].type, Is.EqualTo(ReplayEventType.Miss));
    }

    [Test]
    public void RecordTopout_TypeIsTopout_NoSpatialFields()
    {
        var r = new ReplayRecorder();
        r.RecordTopout(simTime: 42.0f);
        var e = r.Events[0];
        Assert.That(e.type, Is.EqualTo(ReplayEventType.Topout));
        Assert.That(e.simTime, Is.EqualTo(42.0f));
        Assert.That(e.cellX, Is.Null);
        Assert.That(e.cellY, Is.Null);
        Assert.That(e.posX, Is.Null);
        Assert.That(e.posY, Is.Null);
    }

    [Test]
    public void Mixed_World_And_Cell_Variants_Coexist_With_Monotonic_Seq()
    {
        var r = new ReplayRecorder();
        r.RecordSessionStart(); // seq 0
        r.RecordClear(1.5f, 2.5f); // seq 1, world-pos
        r.RecordClearAtCell(1.0f, 3, 4); // seq 2, cell+sim
        r.RecordTopout(2.0f); // seq 3

        Assert.That(r.Events.Count, Is.EqualTo(4));
        for (int i = 0; i < r.Events.Count; i++)
            Assert.That(r.Events[i].seq, Is.EqualTo(i));
    }
}
