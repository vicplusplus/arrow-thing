using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;

[TestFixture]
public class ReplayStorageSizeTests
{
    /// <summary>
    /// Generates a board, simulates a full clear sequence, serializes the resulting
    /// ReplayData to JSON, and logs the byte size. Run manually via Test Runner to
    /// inform compression decisions.
    /// </summary>
    [Explicit]
    [TestCase(10, 10)]
    [TestCase(20, 20)]
    [TestCase(40, 40)]
    [TestCase(100, 100)]
    [TestCase(200, 200)]
    public void MeasureReplaySize(int width, int height)
    {
        var board = new Board(width, height);
        TestBoardHelper.FillBoard(board, width > 20 ? 10 : 5, 42);

        int arrowCount = board.Arrows.Count;

        // Build board snapshot
        var snapshot = new List<List<Cell>>();
        foreach (var arrow in board.Arrows)
            snapshot.Add(new List<Cell>(arrow.Cells));

        // Simulate clear sequence and build replay events
        var recorder = new ReplayRecorder();
        recorder.RecordSessionStart();
        recorder.RecordStartSolve();

        // Clear all arrows in a valid order
        int cleared = 0;
        while (board.Arrows.Count > 0)
        {
            Arrow toClear = null;
            foreach (var arrow in board.Arrows)
            {
                if (board.IsClearable(arrow))
                {
                    toClear = arrow;
                    break;
                }
            }
            Assert.IsNotNull(toClear, "Board should be fully solvable");

            var head = toClear.HeadCell;
            recorder.RecordClear(head.X, head.Y);
            board.RemoveArrow(toClear);
            cleared++;
        }

        recorder.RecordEndSolve();

        var data = recorder.ToReplayData(
            "test-game",
            42,
            width,
            height,
            width > 20 ? 10 : 5,
            15f,
            boardSnapshot: snapshot,
            finalTime: 60.0,
            gameVersion: "1.0.0"
        );

        string json = data.ToJson();
        int byteSize = Encoding.UTF8.GetByteCount(json);
        double kb = byteSize / 1024.0;

        // Compress with GZip
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int compressedSize;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                gz.Write(jsonBytes, 0, jsonBytes.Length);
            compressedSize = (int)ms.Length;
        }

        double compressedKb = compressedSize / 1024.0;
        double ratio = (double)compressedSize / byteSize * 100;

        // Log results
        TestContext.WriteLine($"Board: {width}x{height}");
        TestContext.WriteLine($"  Arrows: {arrowCount}");
        TestContext.WriteLine($"  Clear events: {cleared}");
        TestContext.WriteLine($"  JSON size: {byteSize:N0} bytes ({kb:F1} KB)");
        TestContext.WriteLine(
            $"  GZip size: {compressedSize:N0} bytes ({compressedKb:F1} KB) — {ratio:F1}% of original"
        );
        TestContext.WriteLine(
            $"  Per 50 entries: raw {kb * 50:F1} KB, gzip {compressedKb * 50:F1} KB"
        );

        // Sanity check
        Assert.Greater(byteSize, 0);
        Assert.Less(compressedSize, byteSize, "GZip should reduce size");
    }
}
