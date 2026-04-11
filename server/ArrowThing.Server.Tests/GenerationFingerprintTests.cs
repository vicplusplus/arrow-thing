using Xunit;
using Xunit.Abstractions;

namespace ArrowThing.Server.Tests;

/// <summary>
/// Diagnostic tests that output generation fingerprints for cross-platform comparison.
/// Run these on .NET and compare output with Unity's GenerationFingerprintTests.
/// </summary>
public class GenerationFingerprintTests
{
    private readonly ITestOutputHelper _output;

    public GenerationFingerprintTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PortableRandom_Sequence()
    {
        var rng = new PortableRandom(42);
        for (int i = 0; i < 20; i++)
            _output.WriteLine($"  rng[{i}] = {rng.NextInt(0, 10000)}");
    }

    [Fact]
    public void SeedDerivation()
    {
        foreach (int seed in new[] { 0, 1, 42, -1, int.MaxValue, int.MinValue })
        {
            var seedRng = new PortableRandom((uint)seed);
            int derived = seedRng.NextInt(1, int.MaxValue);
            _output.WriteLine($"seed={seed} (uint)seed={(uint)seed} derived={derived}");
        }
    }

    [Theory]
    [InlineData(42)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(123456)]
    public void BoardFingerprint_10x10(int seed)
    {
        var board = new Board(10, 10);
        TestBoardHelper.FillBoard(board, 5, seed);

        _output.WriteLine($"seed={seed} arrows={board.Arrows.Count}");
        for (int i = 0; i < board.Arrows.Count; i++)
        {
            var a = board.Arrows[i];
            var head = a.HeadCell;
            _output.WriteLine($"  [{i}] ({head.X},{head.Y}) {a.HeadDirection} len={a.Cells.Count}");
        }
    }
}
