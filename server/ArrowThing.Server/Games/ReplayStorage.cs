using ArrowThing.Server.Models;

namespace ArrowThing.Server.Games;

/// <summary>
/// Abstracts the dual-column replay storage introduced in Phase 4.
///
/// New writes go to <see cref="Score.ReplayJsonGz"/> (gzipped bytea) and
/// clear <see cref="Score.ReplayJson"/>. Reads prefer <see cref="Score.ReplayJsonGz"/>
/// and fall back to <see cref="Score.ReplayJson"/> for legacy rows that
/// haven't been re-saved since Phase 4.
/// </summary>
public static class ReplayStorage
{
    /// <summary>Write the canonical replay payload, picking the new column.</summary>
    public static void Set(Score score, string replayJson)
    {
        score.ReplayJsonGz = GzipUtil.Compress(replayJson);
        // Empty out the legacy text column so we don't keep two copies of
        // the same row's payload around forever. EF treats "" as a valid
        // non-null value (the column is NOT NULL).
        score.ReplayJson = "";
    }

    /// <summary>Read the canonical replay payload, falling back to legacy.</summary>
    public static string Get(Score score)
    {
        if (score.ReplayJsonGz != null && score.ReplayJsonGz.Length > 0)
            return GzipUtil.Decompress(score.ReplayJsonGz);
        return score.ReplayJson ?? "";
    }
}
