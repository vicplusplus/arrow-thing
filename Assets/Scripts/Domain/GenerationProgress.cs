using System;

/// <summary>
/// Shared progress calculations for <see cref="BoardGeneration.FillBoardIncremental"/>.
/// Used by both the client (<c>GameController</c>) for the loading bar and the
/// server (<c>LobbyGenerationWorker</c>) for WebSocket progress broadcasts.
///
/// BoardGeneration runs three domain phases: Generating → Compacting → Finalizing.
/// The client has an extra client-only view-rebuild step between Compacting and
/// Finalizing; the server doesn't need it, so phase boundaries differ.
///
/// Weights are tuned from PlayMode GenerationProfiler data (300x300).
/// </summary>
public static class GenerationProgress
{
    // Generation depletion is back-loaded in wall time; raising it to this
    // exponent keeps the bar tracking linearly against real elapsed time.
    public const float GenProgressExponent = 0.7f;

    /// <summary>Client-side phase boundaries (includes view-rebuild phase).</summary>
    public readonly struct Weights
    {
        public readonly float GenEnd;
        public readonly float CompactEnd;
        public readonly float RebuildEnd;

        public Weights(float genEnd, float compactEnd, float rebuildEnd)
        {
            GenEnd = genEnd;
            CompactEnd = compactEnd;
            RebuildEnd = rebuildEnd;
        }
    }

    /// <summary>Client: Gen 0-71%, Compact 71-79%, RebuildViews 79-92%, Finalize 92-100%.</summary>
    public static readonly Weights ClientWeights = new Weights(0.71f, 0.79f, 0.92f);

    /// <summary>Server (no view rebuild): Gen 0-77%, Compact 77-86%, Finalize 86-100%.
    /// RebuildEnd == CompactEnd so the rebuild slice collapses to zero.</summary>
    public static readonly Weights ServerWeights = new Weights(0.77f, 0.86f, 0.86f);

    /// <summary>
    /// Progress during the Generating phase. <paramref name="depletion"/> is
    /// the fraction of initial candidates consumed, i.e.
    /// <c>1 - remaining / initial</c> (0..1).
    /// </summary>
    public static float ForGenerating(float depletion, Weights w)
    {
        float clamped = depletion < 0f ? 0f : (depletion > 1f ? 1f : depletion);
        return w.GenEnd * (float)Math.Pow(clamped, GenProgressExponent);
    }

    /// <summary>
    /// Progress during the Compacting phase. Takes both a merge-ratio and a
    /// wall-time ratio (server can pass 0 for the time ratio if it doesn't
    /// track it) and uses whichever is further along — merges slow down at
    /// the end of compaction, so wall-time keeps the bar moving.
    /// </summary>
    /// <param name="mergesCompleted">Arrows removed since compaction started.</param>
    /// <param name="arrowsBeforeCompaction">Count right before Compacting began.</param>
    /// <param name="timeRatio">0..1 wall-clock ratio of elapsed / expected.</param>
    public static float ForCompacting(
        int mergesCompleted,
        int arrowsBeforeCompaction,
        float timeRatio,
        Weights w
    )
    {
        // Merge-ratio saturates early (observed merges are ~12-15% of arrows).
        float mergeRatio =
            arrowsBeforeCompaction > 0
                ? ClampUnit((float)mergesCompleted / (arrowsBeforeCompaction * 0.12f))
                : 1f;
        float ratio = Math.Max(mergeRatio, ClampUnit(timeRatio));
        return w.GenEnd + (w.CompactEnd - w.GenEnd) * ratio;
    }

    /// <summary>
    /// Expected compacting duration in seconds, fit from measured data:
    /// <c>5e-5 * arrows^1.78 / 1000</c>. Use for the wall-time fallback in
    /// <see cref="ForCompacting"/>.
    /// </summary>
    public static float ExpectedCompactSeconds(int arrowsBeforeCompaction)
    {
        return 5e-5f * (float)Math.Pow(arrowsBeforeCompaction, 1.78f) / 1000f;
    }

    /// <summary>
    /// Progress during the client-only view-rebuild phase. <paramref name="rebuildRatio"/>
    /// is the fraction of arrow views re-instantiated (0..1). Server callers
    /// don't use this — their weights collapse RebuildEnd to CompactEnd.
    /// </summary>
    public static float ForRebuildingViews(float rebuildRatio, Weights w)
    {
        return w.CompactEnd + (w.RebuildEnd - w.CompactEnd) * ClampUnit(rebuildRatio);
    }

    /// <summary>
    /// Progress during the Finalizing phase. <paramref name="finalizeRatio"/>
    /// is finalized/total (0..1). End is 1.0 regardless of weights profile.
    /// </summary>
    public static float ForFinalizing(float finalizeRatio, Weights w)
    {
        return w.RebuildEnd + (1f - w.RebuildEnd) * ClampUnit(finalizeRatio);
    }

    private static float ClampUnit(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
