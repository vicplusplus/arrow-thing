using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;

[TestFixture, Category("Performance"), Explicit("Run manually — these are slow benchmarks")]
public class PerformanceTests
{
    private static void Run(int w, int h, int maxLen, int seed = 0)
    {
        var board = new Board(w, h);
        var sw = Stopwatch.StartNew();
        TestBoardHelper.FillBoard(board, maxLen, seed);
        sw.Stop();
        UnityEngine.Debug.Log(
            $"{w}x{h}  maxLen={maxLen}  arrows={board.Arrows.Count}  cells={TotalCells(board)}  time={sw.ElapsedMilliseconds}ms"
        );
    }

    private static int TotalCells(Board board)
    {
        int n = 0;
        foreach (var a in board.Arrows)
            n += a.Cells.Count;
        return n;
    }

    [Test]
    public void Perf_50x50_VeryLong() => Run(50, 50, 50);

    [Test]
    public void Perf_50x50_Short() => Run(50, 50, 5);

    [Test]
    public void Perf_50x50_Long() => Run(50, 50, 20);

    [Test]
    public void Perf_200x200_Short() => Run(200, 200, 5);

    [Test]
    public void Perf_200x200_Long() => Run(200, 200, 20);

    [Test]
    public void Perf_200x200_VeryLong() => Run(200, 200, 50);

    [Test]
    public void Perf_200x200_MaxWidth() => Run(200, 200, 300);

    [Test]
    public void Solvability_500Seeds_10x10()
    {
        int totalArrows = 0;
        var sw = Stopwatch.StartNew();
        for (int seed = 0; seed < 500; seed++)
        {
            var board = new Board(10, 10);
            TestBoardHelper.FillBoard(board, 5, seed);
            totalArrows += board.Arrows.Count;
            AssertFullyClearable(board, seed);
        }
        sw.Stop();
        UnityEngine.Debug.Log(
            $"500 seeds 10x10: {totalArrows} total arrows, all solvable, {sw.ElapsedMilliseconds}ms"
        );
    }

    [Test]
    public void Solvability_100Seeds_20x20()
    {
        int totalArrows = 0;
        var sw = Stopwatch.StartNew();
        for (int seed = 0; seed < 100; seed++)
        {
            var board = new Board(20, 20);
            TestBoardHelper.FillBoard(board, 10, seed);
            totalArrows += board.Arrows.Count;
            AssertFullyClearable(board, seed);
        }
        sw.Stop();
        UnityEngine.Debug.Log(
            $"100 seeds 20x20: {totalArrows} total arrows, all solvable, {sw.ElapsedMilliseconds}ms"
        );
    }

    [Test]
    public void Solvability_20Seeds_50x50()
    {
        int totalArrows = 0;
        var sw = Stopwatch.StartNew();
        for (int seed = 0; seed < 20; seed++)
        {
            var board = new Board(50, 50);
            TestBoardHelper.FillBoard(board, 20, seed);
            totalArrows += board.Arrows.Count;
            AssertFullyClearable(board, seed);
        }
        sw.Stop();
        UnityEngine.Debug.Log(
            $"20 seeds 50x50: {totalArrows} total arrows, all solvable, {sw.ElapsedMilliseconds}ms"
        );
    }

    // ── Candidate depletion curve profiling ──────────────────────────────

    /// <summary>
    /// Profiles candidate depletion vs wall time (per-arrow granularity).
    /// Outputs a CSV to the console and 5%-interval buckets for visual analysis.
    /// Run manually and inspect the Unity console output.
    /// </summary>
    [Test]
    public void ProfileDepletionCurve_DumpData()
    {
        var configs = new[] { (100, 100, 50, 1), (200, 200, 50, 1) };

        foreach (var (w, h, maxLen, seedCount) in configs)
        {
            for (int s = 0; s < seedCount; s++)
            {
                var board = new Board(w, h);
                var sw = Stopwatch.StartNew();
                var gen = BoardGeneration.FillBoardIncremental(board, maxLen, s);

                int initial = 0;
                var samples = new List<(double ms, float raw, int arrowCount, int cellCount)>();
                int prevArrows = 0;
                var lengthCounts = new Dictionary<int, int>(); // length -> count

                while (gen.MoveNext())
                {
                    if (initial == 0)
                        initial = board.InitialCandidateCount;
                    float raw =
                        initial > 0 ? 1f - (float)board.RemainingCandidateCount / initial : 0f;

                    // Track arrow lengths as they're placed
                    int curArrows = board.Arrows.Count;
                    for (int a = prevArrows; a < curArrows; a++)
                    {
                        int len = board.Arrows[a].Cells.Count;
                        if (!lengthCounts.ContainsKey(len))
                            lengthCounts[len] = 0;
                        lengthCounts[len]++;
                    }
                    prevArrows = curArrows;

                    samples.Add(
                        (sw.Elapsed.TotalMilliseconds, raw, curArrows, board.OccupiedCellCount)
                    );
                }
                sw.Stop();
                int finalArrows = board.Arrows.Count;
                int finalCells = board.OccupiedCellCount;
                int totalCells = w * h;
                samples.Add((sw.Elapsed.TotalMilliseconds, 1f, finalArrows, finalCells));

                double totalMs = samples[samples.Count - 1].ms;

                // 5%-interval buckets
                int bucketCount = 20;
                var bucketRawSum = new double[bucketCount + 1];
                var bucketArrowSum = new double[bucketCount + 1];
                var bucketCellSum = new double[bucketCount + 1];
                var bucketN = new int[bucketCount + 1];
                foreach (var (ms, raw, ac, cc) in samples)
                {
                    int b = Math.Min((int)(ms / totalMs * bucketCount), bucketCount);
                    bucketRawSum[b] += raw;
                    bucketArrowSum[b] += (float)ac / finalArrows;
                    bucketCellSum[b] += (float)cc / finalCells;
                    bucketN[b]++;
                }

                string header =
                    $"\n=== {w}x{h} seed={s}: {finalArrows} arrows, {finalCells}/{totalCells} cells ({100f * finalCells / totalCells:F1}%), {totalMs:F0}ms ===";
                string rawLine = "time% -> candidateDepletion:  ";
                string arrowLine = "time% -> arrowProgress:       ";
                string cellLine = "time% -> cellProgress:        ";
                for (int i = 0; i <= bucketCount; i++)
                {
                    double avgRaw = bucketN[i] > 0 ? bucketRawSum[i] / bucketN[i] : 0;
                    double avgArr = bucketN[i] > 0 ? bucketArrowSum[i] / bucketN[i] : 0;
                    double avgCell = bucketN[i] > 0 ? bucketCellSum[i] / bucketN[i] : 0;
                    rawLine += $"{i * 5}%={avgRaw:F4}  ";
                    arrowLine += $"{i * 5}%={avgArr:F4}  ";
                    cellLine += $"{i * 5}%={avgCell:F4}  ";
                }

                // Length distribution
                var lengths = new List<int>(lengthCounts.Keys);
                lengths.Sort();
                string lengthDist = "Arrow length distribution: ";
                float avgLength = 0f;
                foreach (int len in lengths)
                {
                    lengthDist += $"{len}cells={lengthCounts[len]}  ";
                    avgLength += len * lengthCounts[len];
                }
                avgLength /= finalArrows;
                lengthDist += $"(avg={avgLength:F1})";

                // CSV with all signals
                int step = Math.Max(1, samples.Count / 100);
                string csv = "csv: ms,timeNorm,candidateDepletion,arrowProgress,cellProgress";
                for (int i = 0; i < samples.Count; i += step)
                {
                    var (ms, raw, ac, cc) = samples[i];
                    csv +=
                        $"\n{ms:F1},{ms / totalMs:F6},{raw:F6},{(float)ac / finalArrows:F6},{(float)cc / finalCells:F6}";
                }

                UnityEngine.Debug.Log(
                    header
                        + "\n"
                        + rawLine
                        + "\n"
                        + arrowLine
                        + "\n"
                        + cellLine
                        + "\n"
                        + lengthDist
                        + "\n"
                        + csv
                );
            }
        }

        Assert.Pass("Data dumped — inspect console output");
    }

    // ── Per-phase timing profile ─────────────────────────────────────────

    /// <summary>
    /// Measures wall-time breakdown across generation / compaction / finalization
    /// and samples progress within each phase. Output feeds loading-bar curve tuning.
    /// Runs 100x100, 200x200, and 300x300 with a single seed each.
    /// </summary>
    [Test]
    public void ProfilePhaseTimings_DumpData()
    {
        var configs = new[] { (100, 100, 50), (200, 200, 50), (300, 300, 50) };

        foreach (var (w, h, maxLen) in configs)
        {
            var board = new Board(w, h);
            var gen = BoardGeneration.FillBoardIncremental(board, maxLen, 0);

            var phase = GenerationPhase.Generating;
            var swTotal = Stopwatch.StartNew();

            double genStartMs = 0;
            double compactStartMs = -1;
            double finalizeStartMs = -1;

            int arrowsAtCompactStart = 0;

            // (phaseIndex, elapsedMs, progressWithinPhaseCount) — phaseIndex:
            // 0=generating (count=arrows placed), 1=compacting (count=merges done),
            // 2=finalizing (count=arrows finalized)
            var samples = new List<(int phase, double ms, int count)>();
            // Parallel series for gen phase: candidate depletion at each sample.
            // Using 1e6 * (1 - remaining/initial) stored as int for bucket reuse.
            var genDepletionSamples = new List<(double ms, int depletionScaled)>();

            while (gen.MoveNext())
            {
                double now = swTotal.Elapsed.TotalMilliseconds;

                if (gen.Current is GenerationPhase next)
                {
                    if (next == GenerationPhase.Compacting)
                    {
                        compactStartMs = now;
                        arrowsAtCompactStart = board.Arrows.Count;
                    }
                    else if (next == GenerationPhase.Finalizing)
                    {
                        finalizeStartMs = now;
                    }
                    phase = next;
                    continue;
                }

                switch (phase)
                {
                    case GenerationPhase.Generating:
                    {
                        // yield return null per arrow placed
                        samples.Add((0, now, board.Arrows.Count));
                        int initial = board.InitialCandidateCount;
                        int remaining = board.RemainingCandidateCount;
                        float depletion = initial > 0 ? 1f - (float)remaining / initial : 0f;
                        genDepletionSamples.Add((now, (int)(depletion * 1_000_000f)));
                        break;
                    }
                    case GenerationPhase.Compacting:
                        // yield return int (cumulative merge count)
                        if (gen.Current is int mergeCount)
                            samples.Add((1, now, mergeCount));
                        break;
                    case GenerationPhase.Finalizing:
                        // yield return int (arrows finalized so far)
                        if (gen.Current is int finalized)
                            samples.Add((2, now, finalized));
                        break;
                }
            }
            swTotal.Stop();
            double totalMs = swTotal.Elapsed.TotalMilliseconds;

            // Handle edge cases where phases didn't happen
            if (compactStartMs < 0)
                compactStartMs = totalMs;
            if (finalizeStartMs < 0)
                finalizeStartMs = totalMs;

            double genMs = compactStartMs - genStartMs;
            double compactMs = finalizeStartMs - compactStartMs;
            double finalizeMs = totalMs - finalizeStartMs;

            int finalArrows = board.Arrows.Count;
            int totalMerges = arrowsAtCompactStart - finalArrows;

            // Count samples per phase
            int genN = 0,
                compN = 0,
                finN = 0;
            foreach (var s in samples)
            {
                if (s.phase == 0)
                    genN++;
                else if (s.phase == 1)
                    compN++;
                else if (s.phase == 2)
                    finN++;
            }

            // Per-phase 10% time buckets of (count progress within that phase)
            var genBuckets = BuildBuckets(samples, 0, genStartMs, compactStartMs, finalArrows);
            var genDepletionBuckets = BuildBucketsRaw(
                genDepletionSamples,
                genStartMs,
                compactStartMs,
                1_000_000f
            );
            var compBuckets = BuildBuckets(
                samples,
                1,
                compactStartMs,
                finalizeStartMs,
                Math.Max(1, totalMerges)
            );
            var finBuckets = BuildBuckets(
                samples,
                2,
                finalizeStartMs,
                totalMs,
                Math.Max(1, finalArrows)
            );

            string header =
                $"\n=== {w}x{h} maxLen={maxLen}: "
                + $"total={totalMs:F0}ms  "
                + $"gen={genMs:F0}ms ({100 * genMs / totalMs:F1}%)  "
                + $"compact={compactMs:F0}ms ({100 * compactMs / totalMs:F1}%)  "
                + $"finalize={finalizeMs:F0}ms ({100 * finalizeMs / totalMs:F1}%) ===\n"
                + $"arrows={finalArrows} (placed={arrowsAtCompactStart}, merged={totalMerges})\n"
                + $"samples: gen={genN} compact={compN} finalize={finN}";

            string genLine = "gen     (phase time% -> arrow%):     " + FormatBuckets(genBuckets);
            string genDepLine =
                "gen     (phase time% -> candidateDepletion%): "
                + FormatBuckets(genDepletionBuckets);
            string compLine = "compact (phase time% -> merge%):     " + FormatBuckets(compBuckets);
            string finLine = "finalize(phase time% -> finalized%): " + FormatBuckets(finBuckets);

            UnityEngine.Debug.Log(
                header + "\n" + genLine + "\n" + genDepLine + "\n" + compLine + "\n" + finLine
            );
        }

        Assert.Pass("Data dumped — inspect console output");
    }

    private static double[] BuildBuckets(
        List<(int phase, double ms, int count)> samples,
        int phase,
        double phaseStartMs,
        double phaseEndMs,
        int finalCount
    )
    {
        const int bucketCount = 10;
        var sum = new double[bucketCount + 1];
        var n = new int[bucketCount + 1];
        double dur = Math.Max(0.0001, phaseEndMs - phaseStartMs);
        foreach (var s in samples)
        {
            if (s.phase != phase)
                continue;
            double t = (s.ms - phaseStartMs) / dur;
            int b = Math.Clamp((int)(t * bucketCount), 0, bucketCount);
            sum[b] += (double)s.count / finalCount;
            n[b]++;
        }
        var avg = new double[bucketCount + 1];
        for (int i = 0; i <= bucketCount; i++)
            avg[i] = n[i] > 0 ? sum[i] / n[i] : double.NaN;
        return avg;
    }

    private static double[] BuildBucketsRaw(
        List<(double ms, int valueScaled)> samples,
        double startMs,
        double endMs,
        float scale
    )
    {
        const int bucketCount = 10;
        var sum = new double[bucketCount + 1];
        var n = new int[bucketCount + 1];
        double dur = Math.Max(0.0001, endMs - startMs);
        foreach (var s in samples)
        {
            double t = (s.ms - startMs) / dur;
            int b = Math.Clamp((int)(t * bucketCount), 0, bucketCount);
            sum[b] += s.valueScaled / scale;
            n[b]++;
        }
        var avg = new double[bucketCount + 1];
        for (int i = 0; i <= bucketCount; i++)
            avg[i] = n[i] > 0 ? sum[i] / n[i] : double.NaN;
        return avg;
    }

    private static string FormatBuckets(double[] buckets)
    {
        var parts = new List<string>();
        for (int i = 0; i < buckets.Length; i++)
        {
            int pct = i * 10;
            if (double.IsNaN(buckets[i]))
                parts.Add($"{pct}%=--");
            else
                parts.Add($"{pct}%={buckets[i]:F3}");
        }
        return string.Join("  ", parts);
    }

    private static void AssertFullyClearable(Board board, int seed)
    {
        int initial = board.Arrows.Count;
        Assert.That(initial, Is.GreaterThan(0), $"Seed {seed}: no arrows generated.");

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
            Assert.That(
                toClear,
                Is.Not.Null,
                $"Seed {seed}: deadlock with {board.Arrows.Count}/{initial} arrows remaining."
            );
            board.RemoveArrow(toClear);
            cleared++;
        }
    }
}
