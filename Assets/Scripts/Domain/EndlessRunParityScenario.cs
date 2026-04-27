using System;

/// <summary>
/// Canonical determinism scenario for <see cref="EndlessRun"/>. Drives a
/// fixed-seed run through a deterministic script of <c>Advance</c> /
/// <c>HandleTap</c> calls and folds every observable post-run quantity
/// into a single 64-bit FNV-1a hash. Same inputs MUST produce the same
/// hash on Mono (Unity editor, IL2CPP, WebGL) and .NET (server runtime).
///
/// <para>This is the parity safety net for the float-determinism question
/// in the endless verifier: <see cref="EndlessRun"/> uses <c>Math.Cos</c>,
/// <c>Math.Pow</c>, <c>Math.Round</c>, and accumulated <c>float</c> /
/// <c>double</c> arithmetic. If any of those produce different bits between
/// runtimes, the server-side replay verifier would flag legitimate replays
/// as cheats. <see cref="ExpectedHashV1"/> is computed once on .NET and
/// frozen; both client (NUnit EditMode) and server (xUnit) tests run
/// <see cref="RunAndHash"/> and compare against the constant.</para>
///
/// <para><b>What's hashed</b> (in order):</para>
/// <list type="number">
///   <item><see cref="EndlessRun.ClearCount"/></item>
///   <item><see cref="EndlessRun.LongestCombo"/></item>
///   <item><see cref="EndlessRun.RunDurationSeconds"/> (raw float bits)</item>
///   <item><see cref="EndlessRun.IsActive"/></item>
///   <item><see cref="EndlessRun.ActiveShortfall"/></item>
///   <item>Meter: count, then each <see cref="EndlessMeterEntry.Size"/> +
///   <see cref="EndlessMeterEntry.ColorIndex"/> in order</item>
///   <item>Board arrows: count, then for each arrow head (X, Y) and
///   <c>(int)HeadDirection</c> in <see cref="Board.Arrows"/> order</item>
///   <item>Pending arrows: count, then for each pending head
///   (X, Y) plus length</item>
/// </list>
///
/// <para><b>Procedure when bumping <see cref="EndlessTuning"/></b>: any
/// tuning change that affects replay outcome will change the hash.
/// (1) Run the server xUnit test — it will fail with the new actual hash.
/// (2) Replace <see cref="ExpectedHashV1"/> with the new value.
/// (3) Run the EditMode test in Unity — it must match. If Mono disagrees,
/// that is the float-determinism bug this whole apparatus exists to catch:
/// fix it before merging the tuning change.</para>
/// </summary>
public static class EndlessRunParityScenario
{
    public const int Seed = 0x5EEDF00D;
    public const int BoardWidth = 10;
    public const int BoardHeight = 10;
    public const int PaletteCount = 6;
    public const int TuningVersion = 1;

    /// <summary>
    /// FNV-1a 64-bit hash of every observable post-run quantity. Same
    /// inputs must produce the same hash on Mono and .NET. Bootstrapped
    /// from a .NET run; the EditMode test catches Mono drift if any.
    /// </summary>
    /// <remarks>
    /// Bootstrapped from a .NET 10 server-test run. If you change endless
    /// tuning or the run loop, this constant will go stale — see
    /// "Procedure when bumping <see cref="EndlessTuning"/>" in the type doc.
    /// </remarks>
    public const ulong ExpectedHashV1 = 0xAF62214675C7C89EUL;

    /// <summary>
    /// Constructs the canonical run, drives it through the canonical
    /// script, and returns a 64-bit FNV-1a hash of every observable.
    /// </summary>
    public static ulong RunAndHash()
    {
        var board = new Board(BoardWidth, BoardHeight);
        var run = new EndlessRun(
            board,
            Seed,
            PaletteCount,
            EndlessTuning.ForVersion(TuningVersion)
        );

        // Drive ~45 simulated seconds in 0.1s steps with a periodic tap
        // sweep across the board so HandleTap exercises Missed / Blocked
        // / Cleared paths and feeds clears back into the shortfall logic.
        // Step count picked to exercise multiple push ticks + commits but
        // run in well under a second on .NET.
        const int steps = 450;
        const float dt = 0.1f;
        for (int i = 0; i < steps; i++)
        {
            run.Advance(dt);
            // Every 7th step, tap a deterministic cell. The pattern walks
            // the board column-by-column so we hit a mix of empty cells,
            // pending arrows, and committed arrows.
            if (i % 7 == 0)
            {
                int n = i / 7;
                int x = n % BoardWidth;
                int y = (n / BoardWidth) % BoardHeight;
                run.HandleTap(x, y);
            }
            if (!run.IsActive)
                break;
        }

        return Hash(run);
    }

    private static ulong Hash(EndlessRun run)
    {
        var h = new Fnv1a64();
        h.MixInt32(run.ClearCount);
        h.MixInt32(run.LongestCombo);
        h.MixFloatBits(run.RunDurationSeconds);
        h.MixByte((byte)(run.IsActive ? 1 : 0));
        h.MixInt32(run.ActiveShortfall);

        h.MixInt32(run.Meter.Count);
        for (int i = 0; i < run.Meter.Count; i++)
        {
            var e = run.Meter[i];
            h.MixInt32(e.Size);
            h.MixInt32(e.ColorIndex);
        }

        var arrows = run.Board.Arrows;
        h.MixInt32(arrows.Count);
        for (int i = 0; i < arrows.Count; i++)
        {
            var a = arrows[i];
            var head = a.HeadCell;
            h.MixInt32(head.X);
            h.MixInt32(head.Y);
            h.MixInt32((int)a.HeadDirection);
            h.MixInt32(a.Cells.Count);
        }

        var pending = run.PendingArrows;
        h.MixInt32(pending.Count);
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            var head = p.Arrow.HeadCell;
            h.MixInt32(head.X);
            h.MixInt32(head.Y);
            h.MixInt32(p.Arrow.Cells.Count);
        }

        return h.Value;
    }

    // ---- FNV-1a 64-bit -----------------------------------------------------
    //
    // Hand-rolled so we don't depend on string.GetHashCode (randomized in
    // .NET) or HashCode (also randomized). FNV-1a is bit-stable, fast, and
    // trivially portable.

    private struct Fnv1a64
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        private ulong _h;

        public ulong Value => _h == 0UL ? Offset : _h;

        public void MixByte(byte b)
        {
            if (_h == 0UL)
                _h = Offset;
            _h ^= b;
            _h *= Prime;
        }

        public void MixInt32(int v)
        {
            uint u = (uint)v;
            MixByte((byte)(u & 0xFF));
            MixByte((byte)((u >> 8) & 0xFF));
            MixByte((byte)((u >> 16) & 0xFF));
            MixByte((byte)((u >> 24) & 0xFF));
        }

        public void MixFloatBits(float f)
        {
            // BitConverter.SingleToInt32Bits is netstandard2.1+ and matches
            // on Mono and .NET (both use IEEE 754 single-precision storage).
            MixInt32(BitConverter.SingleToInt32Bits(f));
        }
    }
}
