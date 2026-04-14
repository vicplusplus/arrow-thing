using System.Collections.Generic;
using Newtonsoft.Json;
using NUnit.Framework;

[TestFixture]
public class ReplayDataMigrationTests
{
    [Test]
    public void V5RoundTrip_PreservesSnapshot()
    {
        var original = new ReplayData
        {
            version = 5,
            gameId = "test-game",
            seed = 42,
            boardWidth = 10,
            boardHeight = 10,
            maxArrowLength = 40,
            inspectionDuration = 15f,
        };
        original.SetSnapshotArrows(
            new List<IReadOnlyList<Cell>>
            {
                new List<Cell> { new Cell(0, 0), new Cell(0, 1), new Cell(0, 2) },
                new List<Cell> { new Cell(5, 5), new Cell(6, 5) },
            }
        );

        var json = original.ToJson();
        var loaded = JsonConvert.DeserializeObject<ReplayData>(json);

        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.version, Is.EqualTo(5));
        var arrows = loaded.GetSnapshotArrows();
        Assert.That(arrows, Has.Count.EqualTo(2));
        Assert.That(arrows[0], Has.Count.EqualTo(3));
        Assert.That(arrows[0][2], Is.EqualTo(new Cell(0, 2)));
    }

    [Test]
    public void V4LegacyJson_LoadsViaConverter()
    {
        // Hand-built v4 JSON with the legacy List<List<Cell>> snapshot shape.
        var v4Json =
            "{"
            + "\"version\":4,"
            + "\"gameId\":\"legacy-game\","
            + "\"seed\":99,"
            + "\"boardWidth\":5,"
            + "\"boardHeight\":5,"
            + "\"maxArrowLength\":40,"
            + "\"inspectionDuration\":15.0,"
            + "\"boardSnapshot\":["
            + "[{\"X\":1,\"Y\":1},{\"X\":1,\"Y\":2}],"
            + "[{\"X\":3,\"Y\":3},{\"X\":4,\"Y\":3},{\"X\":4,\"Y\":4}]"
            + "],"
            + "\"events\":[]"
            + "}";

        var loaded = JsonConvert.DeserializeObject<ReplayData>(v4Json);
        Assert.That(loaded, Is.Not.Null);
        Assert.That(loaded.version, Is.EqualTo(4));

        var arrows = loaded.GetSnapshotArrows();
        Assert.That(arrows, Has.Count.EqualTo(2));
        Assert.That(arrows[0][0], Is.EqualTo(new Cell(1, 1)));
        Assert.That(arrows[0][1], Is.EqualTo(new Cell(1, 2)));
        Assert.That(arrows[1], Has.Count.EqualTo(3));
        Assert.That(arrows[1][2], Is.EqualTo(new Cell(4, 4)));
    }

    [Test]
    public void V4LoadedThenResaved_BecomesV5Format()
    {
        var v4Json =
            "{\"version\":4,\"boardSnapshot\":[[{\"X\":0,\"Y\":0},{\"X\":1,\"Y\":0}]],\"events\":[]}";
        var loaded = JsonConvert.DeserializeObject<ReplayData>(v4Json);
        // Resave — boardSnapshot is now a base64 string.
        var resavedJson = loaded.ToJson();
        Assert.That(resavedJson, Does.Contain("\"boardSnapshot\":\""));
        Assert.That(resavedJson, Does.Not.Contain("\"X\":0,\"Y\":0"));

        // Roundtrip again: data preserved.
        var roundtripped = JsonConvert.DeserializeObject<ReplayData>(resavedJson);
        var arrows = roundtripped.GetSnapshotArrows();
        Assert.That(arrows, Has.Count.EqualTo(1));
        Assert.That(arrows[0][0], Is.EqualTo(new Cell(0, 0)));
        Assert.That(arrows[0][1], Is.EqualTo(new Cell(1, 0)));
    }

    [Test]
    public void GetSnapshotArrows_NullSnapshot_ReturnsNull()
    {
        var data = new ReplayData { version = 5 };
        Assert.That(data.GetSnapshotArrows(), Is.Null);
    }

    [Test]
    public void SetSnapshotArrows_Null_ClearsField()
    {
        var data = new ReplayData { version = 5 };
        data.SetSnapshotArrows(
            new List<IReadOnlyList<Cell>>
            {
                new List<Cell> { new Cell(0, 0), new Cell(0, 1) },
            }
        );
        Assert.That(data.boardSnapshot, Is.Not.Null);

        data.SetSnapshotArrows(null);
        Assert.That(data.boardSnapshot, Is.Null);
    }
}
