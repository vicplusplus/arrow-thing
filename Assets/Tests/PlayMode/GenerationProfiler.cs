using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// PlayMode test that profiles board generation with the full Unity pipeline.
/// Mirrors the exact production code path (FillBoardIncremental + AddArrowView)
/// and measures where time is spent.
///
/// Run from Test Runner (PlayMode tab). Results in TestResults/.
/// </summary>
[TestFixture, Category("Performance"), Explicit("Run manually — captures profiler data")]
public class GenerationProfiler
{
    static readonly ProfilerMarker s_MarkerMoveNext = new(ProfilerCategory.Scripts, "Gen.MoveNext");
    static readonly ProfilerMarker s_MarkerCreateView = new(
        ProfilerCategory.Scripts,
        "Gen.CreateArrowView"
    );

    [UnityTest]
    public IEnumerator Profile_50x50() => RunProfile(50, 50, 20, 0);

    [UnityTest]
    public IEnumerator Profile_100x100() => RunProfile(100, 100, 50, 0);

    [UnityTest]
    public IEnumerator Profile_200x200() => RunProfile(200, 200, 50, 0);

    [UnityTest]
    public IEnumerator Profile_300x300() => RunProfile(300, 300, 50, 0);

    [UnityTest]
    public IEnumerator Profile_400x400() => RunProfile(400, 400, 50, 0);

    private IEnumerator RunProfile(int w, int h, int maxLen, int seed)
    {
        string outputDir = Path.Combine(Application.dataPath, "..", "TestResults");
        Directory.CreateDirectory(outputDir);
        string summaryPath = Path.Combine(outputDir, $"profile_{w}x{h}_seed{seed}_summary.txt");

        // Load VisualSettings
        var settingsArr = Resources.FindObjectsOfTypeAll<VisualSettings>();
        VisualSettings settings = settingsArr.Length > 0 ? settingsArr[0] : null;
        var boardParent = new GameObject("ProfileBoard");

        // Board + generator — exact production code path
        var board = new Board(w, h);
        var generator = BoardGeneration.FillBoardIncremental(board, maxLen, seed);

        float frameBudgetMs = 12f;
        int arrowCount = 0;
        var phase = GenerationPhase.Generating;
        int totalArrows = 0; // captured at finalization transition

        // Accumulators (nanoseconds via Stopwatch ticks * 100)
        long totalMoveNextNs = 0;
        long totalViewNs = 0;
        long totalYieldNs = 0;

        // Phase-split accumulators (domain work only)
        long placementMoveNextNs = 0;
        long placementViewNs = 0;
        long compactionMoveNextNs = 0;
        long rebuildViewNs = 0; // incremental view rebuild after compaction
        long finalizationMoveNextNs = 0;

        // Phase-split WALL TIME (includes yields between frames)
        long placementWallNs = 0;
        long compactionWallNs = 0;
        long rebuildWallNs = 0;
        long finalizationWallNs = 0;

        int placementFrames = 0;
        int compactionFrames = 0;
        int rebuildFrames = 0;
        int finalizationFrames = 0;

        // Track spawned view GameObjects so we can simulate the compact→finalize rebuild
        var spawnedViews = new List<GameObject>();

        // Incremental rebuild state — mirrors GameController.GenerateBoard
        bool rebuildingViews = false;
        List<Arrow> rebuildList = null;
        int rebuildIndex = 0;

        // Per-frame samples
        var frameSamples = new List<FrameSample>(2048);

        var wallSw = Stopwatch.StartNew();

        while (true)
        {
            var frameSw = Stopwatch.StartNew();
            bool done = false;
            int frameArrows = 0;
            long frameMoveNextNs = 0;
            long frameViewNs = 0;
            bool frameHadRebuild = false;

            while (frameSw.ElapsedMilliseconds < frameBudgetMs)
            {
                // Incremental view rebuild (mirrors GameController.GenerateBoard)
                if (rebuildingViews)
                {
                    frameHadRebuild = true;
                    if (rebuildIndex < rebuildList.Count && settings != null)
                    {
                        var vSw = Stopwatch.StartNew();
                        s_MarkerCreateView.Begin();
                        var go = new GameObject($"Arrow_{rebuildIndex}");
                        go.transform.SetParent(boardParent.transform, false);
                        var view = go.AddComponent<ArrowView>();
                        view.Init(rebuildList[rebuildIndex], w, h, settings);
                        spawnedViews.Add(go);
                        s_MarkerCreateView.End();
                        vSw.Stop();
                        long rvNs = vSw.Elapsed.Ticks * 100;
                        frameViewNs += rvNs;
                        rebuildViewNs += rvNs;
                        rebuildIndex++;
                        continue;
                    }
                    // Rebuild done — fall through to generator advance for finalize.
                    rebuildingViews = false;
                    rebuildList = null;
                }

                // MoveNext — this is TryGenerateArrow + AddArrowForGeneration
                // (or FinalizeGenerationIncremental step)
                var mnSw = Stopwatch.StartNew();
                s_MarkerMoveNext.Begin();
                bool hasNext = generator.MoveNext();
                s_MarkerMoveNext.End();
                mnSw.Stop();
                long mnNs = mnSw.Elapsed.Ticks * 100;
                frameMoveNextNs += mnNs;

                if (!hasNext)
                {
                    done = true;
                    break;
                }

                // Detect phase transition
                if (generator.Current is GenerationPhase nextPhase)
                {
                    if (
                        nextPhase == GenerationPhase.Finalizing
                        && phase == GenerationPhase.Compacting
                    )
                    {
                        // Synchronously destroy old views (fast) and start
                        // incremental rebuild over subsequent frame budgets.
                        foreach (var go in spawnedViews)
                            Object.DestroyImmediate(go);
                        spawnedViews.Clear();
                        rebuildList = new List<Arrow>(board.Arrows);
                        rebuildIndex = 0;
                        rebuildingViews = true;
                    }

                    if (nextPhase == GenerationPhase.Finalizing)
                        totalArrows = board.Arrows.Count;
                    phase = nextPhase;
                    continue;
                }

                // ArrowView creation (placement phase only)
                if (phase == GenerationPhase.Generating && settings != null)
                {
                    var vSw = Stopwatch.StartNew();
                    s_MarkerCreateView.Begin();
                    var go = new GameObject($"Arrow_{arrowCount}");
                    go.transform.SetParent(boardParent.transform, false);
                    var view = go.AddComponent<ArrowView>();
                    view.Init(board.Arrows[arrowCount], w, h, settings);
                    spawnedViews.Add(go);
                    s_MarkerCreateView.End();
                    vSw.Stop();
                    frameViewNs += vSw.Elapsed.Ticks * 100;
                    arrowCount++;
                    frameArrows++;
                }
            }

            frameSw.Stop();
            long frameWallNs = frameSw.Elapsed.Ticks * 100;

            totalMoveNextNs += frameMoveNextNs;
            totalViewNs += frameViewNs;

            string phaseLabel;
            if (frameHadRebuild)
            {
                phaseLabel = "rebuild";
                rebuildWallNs += frameWallNs;
                rebuildFrames++;
            }
            else
            {
                switch (phase)
                {
                    case GenerationPhase.Generating:
                        phaseLabel = "placement";
                        placementMoveNextNs += frameMoveNextNs;
                        placementViewNs += frameViewNs;
                        placementWallNs += frameWallNs;
                        placementFrames++;
                        break;
                    case GenerationPhase.Compacting:
                        phaseLabel = "compaction";
                        compactionMoveNextNs += frameMoveNextNs;
                        compactionWallNs += frameWallNs;
                        compactionFrames++;
                        break;
                    default:
                        phaseLabel = "finalization";
                        finalizationMoveNextNs += frameMoveNextNs;
                        finalizationWallNs += frameWallNs;
                        finalizationFrames++;
                        break;
                }
            }

            frameSamples.Add(
                new FrameSample
                {
                    arrowsPlaced = frameArrows,
                    moveNextNs = frameMoveNextNs,
                    createViewNs = frameViewNs,
                    frameNs = frameSw.Elapsed.Ticks * 100,
                    cumulativeArrows = arrowCount,
                    phase = phaseLabel,
                }
            );

            if (done)
                break;

            var yieldSw = Stopwatch.StartNew();
            yield return null;
            yieldSw.Stop();
            long yieldNs = yieldSw.Elapsed.Ticks * 100;
            totalYieldNs += yieldNs;
            // Attribute yield time to the phase that just finished its frame.
            if (frameHadRebuild)
            {
                rebuildWallNs += yieldNs;
            }
            else
            {
                switch (phase)
                {
                    case GenerationPhase.Generating:
                        placementWallNs += yieldNs;
                        break;
                    case GenerationPhase.Compacting:
                        compactionWallNs += yieldNs;
                        break;
                    default:
                        finalizationWallNs += yieldNs;
                        break;
                }
            }
        }

        wallSw.Stop();
        long wallMs = wallSw.ElapsedMilliseconds;
        if (totalArrows == 0)
            totalArrows = board.Arrows.Count;

        // ── Build summary ──────────────────────────────────────────────
        var sb = new StringBuilder();
        float toMs(long ns) => ns / 1_000_000f;
        float pct(long part, long whole) => whole > 0 ? 100f * part / whole : 0f;

        long totalWorkNs = totalMoveNextNs + totalViewNs;

        sb.AppendLine($"=== Generation Profile: {w}x{h}, maxLen={maxLen}, seed={seed} ===");
        sb.AppendLine();
        sb.AppendLine($"Arrows:       {totalArrows}");
        sb.AppendLine(
            $"Cells:        {board.OccupiedCellCount} / {w * h} ({100f * board.OccupiedCellCount / (w * h):F1}%)"
        );
        sb.AppendLine(
            $"Frames:       {placementFrames} placement + {compactionFrames} compaction + {rebuildFrames} rebuild + {finalizationFrames} finalization = {frameSamples.Count} total"
        );
        sb.AppendLine();
        sb.AppendLine("── Wall Clock ────────────────────────────────────────");
        sb.AppendLine($"  Total:              {wallMs, 8}ms");
        sb.AppendLine(
            $"  Work (our code):    {toMs(totalWorkNs), 8:F0}ms  ({pct(totalWorkNs, wallMs * 1_000_000L):F1}%)"
        );
        sb.AppendLine(
            $"  Yield/render:       {toMs(totalYieldNs), 8:F0}ms  ({pct(totalYieldNs, wallMs * 1_000_000L):F1}%)"
        );
        sb.AppendLine();
        // Per-phase wall time — THIS is what the loading bar should track.
        // Includes domain work + view creation + yields/render time.
        long totalWallNs = placementWallNs + compactionWallNs + rebuildWallNs + finalizationWallNs;
        sb.AppendLine("── Wall Time by Phase (feeds loading-bar tuning) ─────");
        sb.AppendLine(
            $"  Placement:          {toMs(placementWallNs), 8:F0}ms  ({pct(placementWallNs, totalWallNs):F1}%)"
        );
        sb.AppendLine(
            $"  Compaction:         {toMs(compactionWallNs), 8:F0}ms  ({pct(compactionWallNs, totalWallNs):F1}%)"
        );
        sb.AppendLine(
            $"  Rebuild views:      {toMs(rebuildWallNs), 8:F0}ms  ({pct(rebuildWallNs, totalWallNs):F1}%)"
        );
        sb.AppendLine(
            $"  Finalization:       {toMs(finalizationWallNs), 8:F0}ms  ({pct(finalizationWallNs, totalWallNs):F1}%)"
        );
        sb.AppendLine();
        sb.AppendLine("── Work Breakdown ────────────────────────────────────");
        sb.AppendLine(
            $"  MoveNext (domain):  {toMs(totalMoveNextNs), 8:F0}ms  ({pct(totalMoveNextNs, totalWorkNs):F1}% of work)"
        );
        sb.AppendLine($"    Placement phase:  {toMs(placementMoveNextNs), 8:F0}ms");
        sb.AppendLine($"    Compaction:       {toMs(compactionMoveNextNs), 8:F0}ms");
        sb.AppendLine($"    Finalization:     {toMs(finalizationMoveNextNs), 8:F0}ms");
        sb.AppendLine(
            $"  ArrowView creation: {toMs(totalViewNs), 8:F0}ms  ({pct(totalViewNs, totalWorkNs):F1}% of work)"
        );
        sb.AppendLine($"    Placement spawns: {toMs(placementViewNs), 8:F0}ms");
        sb.AppendLine($"    Rebuild spawns:   {toMs(rebuildViewNs), 8:F0}ms");
        sb.AppendLine();

        if (totalArrows > 0)
        {
            sb.AppendLine("── Per-Arrow Averages ────────────────────────────────");
            sb.AppendLine(
                $"  MoveNext (placement): {toMs(placementMoveNextNs) / totalArrows:F4}ms/arrow"
            );
            sb.AppendLine($"  ArrowView creation:   {toMs(totalViewNs) / totalArrows:F4}ms/arrow");
            sb.AppendLine(
                $"  Finalization:         {toMs(finalizationMoveNextNs) / totalArrows:F4}ms/arrow"
            );
            sb.AppendLine();
        }

        // Early vs Late analysis
        var pFrames = frameSamples.FindAll(f => f.phase == "placement");
        if (pFrames.Count >= 10)
        {
            int tenPct = pFrames.Count / 10;

            sb.AppendLine("── Early vs Late (first/last 10% of placement frames) ─");
            AppendPhaseSlice(sb, pFrames, 0, tenPct, "Early", toMs);
            AppendPhaseSlice(sb, pFrames, pFrames.Count - tenPct, pFrames.Count, "Late", toMs);
            sb.AppendLine();
        }

        // Frame time percentiles
        if (pFrames.Count > 0)
        {
            var times = new long[pFrames.Count];
            var arrowCounts = new int[pFrames.Count];
            for (int i = 0; i < pFrames.Count; i++)
            {
                times[i] = pFrames[i].frameNs;
                arrowCounts[i] = pFrames[i].arrowsPlaced;
            }
            System.Array.Sort(times);
            System.Array.Sort(arrowCounts);

            sb.AppendLine("── Frame Percentiles (placement) ─────────────────────");
            sb.AppendLine(
                $"  Frame time:    p50={toMs(times[times.Length / 2]):F1}ms  p90={toMs(times[(int)(times.Length * 0.9)]):F1}ms  max={toMs(times[times.Length - 1]):F1}ms"
            );
            sb.AppendLine(
                $"  Arrows/frame:  p50={arrowCounts[arrowCounts.Length / 2]}  p10={arrowCounts[(int)(arrowCounts.Length * 0.1)]}  min={arrowCounts[0]}"
            );
            sb.AppendLine();
        }

        string summary = sb.ToString();
        File.WriteAllText(summaryPath, summary);
        UnityEngine.Debug.Log(summary);
        UnityEngine.Debug.Log($"Written to: {summaryPath}");

        Object.Destroy(boardParent);
        Assert.Pass($"Profile complete — see {summaryPath}");
    }

    private static void AppendPhaseSlice(
        StringBuilder sb,
        List<FrameSample> frames,
        int start,
        int end,
        string label,
        System.Func<long, float> toMs
    )
    {
        long genNs = 0,
            viewNs = 0;
        int arrows = 0;
        for (int i = start; i < end; i++)
        {
            genNs += frames[i].moveNextNs;
            viewNs += frames[i].createViewNs;
            arrows += frames[i].arrowsPlaced;
        }
        sb.AppendLine($"  {label}: {arrows} arrows in {end - start} frames");
        sb.AppendLine($"    MoveNext: {toMs(genNs):F1}ms  ArrowView: {toMs(viewNs):F1}ms");
        if (arrows > 0)
            sb.AppendLine(
                $"    Per arrow: gen={toMs(genNs) / arrows:F4}ms  view={toMs(viewNs) / arrows:F4}ms"
            );
    }

    private struct FrameSample
    {
        public int arrowsPlaced;
        public long moveNextNs;
        public long createViewNs;
        public long frameNs;
        public int cumulativeArrows;
        public string phase;
    }
}
