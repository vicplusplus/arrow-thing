using NUnit.Framework;
using UnityEngine;

/// <summary>
/// Diagnostic tests that output generation fingerprints for cross-platform comparison.
/// Run these in Unity and compare output with the server's GenerationFingerprintTests.
/// Check the Console window for Debug.Log output after running.
/// </summary>
[TestFixture]
public class GenerationFingerprintTests
{
    [Test]
    public void PortableRandom_Sequence()
    {
        var rng = new PortableRandom(42);
        for (int i = 0; i < 20; i++)
            Debug.Log($"  rng[{i}] = {rng.NextInt(0, 10000)}");
    }

    [Test]
    public void SeedDerivation()
    {
        foreach (int seed in new[] { 0, 1, 42, -1, int.MaxValue, int.MinValue })
        {
            var seedRng = new PortableRandom((uint)seed);
            int derived = seedRng.NextInt(1, int.MaxValue);
            Debug.Log($"seed={seed} (uint)seed={(uint)seed} derived={derived}");
        }
    }

    [TestCase(42)]
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(123456)]
    public void BoardFingerprint_10x10(int seed)
    {
        var board = new Board(10, 10);
        TestBoardHelper.FillBoard(board, 5, seed);

        Debug.Log($"seed={seed} arrows={board.Arrows.Count}");
        for (int i = 0; i < board.Arrows.Count; i++)
        {
            var a = board.Arrows[i];
            var head = a.HeadCell;
            Debug.Log($"  [{i}] ({head.X},{head.Y}) {a.HeadDirection} len={a.Cells.Count}");
        }
    }
}
