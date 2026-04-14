using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Full save / replay record for a single game session.
/// Serializes to JSON via Newtonsoft.Json.
/// A <see cref="finalTime"/> of -1 indicates an in-progress (incomplete) game.
///
/// Schema version history:
///   v1 — initial.
///   v2 — added boardSnapshot (List&lt;List&lt;Cell&gt;&gt;).
///   v3 — added gameVersion.
///   v4 — post-RNG rewrite for cross-platform determinism.
///   v5 — boardSnapshot is now a base64-encoded BinarySnapshot blob (single
///        canonical encoder shared with co-op wire transport). v4 readers
///        with cell-array snapshots are still loaded transparently via the
///        custom converter on the boardSnapshot field.
/// </summary>
public sealed class ReplayData
{
    /// <summary>Format version — increment if the schema changes incompatibly.</summary>
    public int version = 5;

    /// <summary>UUID string. Uniquely identifies this game session.</summary>
    public string gameId;

    public int seed;
    public int boardWidth;
    public int boardHeight;
    public int maxArrowLength;

    /// <summary>Inspection phase duration in seconds, as configured when the board was created.</summary>
    public float inspectionDuration;

    /// <summary>Application version at the time of recording (version 3+). Null for older replays.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string gameVersion;

    /// <summary>
    /// Initial arrow configuration, serialized as a base64-encoded BinarySnapshot
    /// arrow table (v5+). For v4 saves, the JSON value is a list of cell arrays —
    /// the custom converter transparently re-encodes that to base64 on read so all
    /// downstream consumers can use <see cref="GetSnapshotArrows"/> uniformly.
    ///
    /// Null for v1 legacy saves — resume falls back to seed-based regeneration.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(BoardSnapshotConverter))]
    public string boardSnapshot;

    /// <summary>Ordered event log (by seq). Never null; always at least one session_start.</summary>
    public List<ReplayEvent> events = new List<ReplayEvent>();

    /// <summary>
    /// Solve time in seconds at board completion. -1 if the game is still in progress.
    /// </summary>
    public double finalTime = -1.0;

    /// <summary>
    /// Decodes <see cref="boardSnapshot"/> into a list of arrow cell paths.
    /// Returns null if no snapshot was recorded.
    /// </summary>
    public List<List<Cell>> GetSnapshotArrows()
    {
        if (string.IsNullOrEmpty(boardSnapshot))
            return null;
        var bytes = Convert.FromBase64String(boardSnapshot);
        return BinarySnapshot.DecodeArrows(bytes);
    }

    /// <summary>
    /// Encodes a list of arrow cell paths into <see cref="boardSnapshot"/>.
    /// </summary>
    public void SetSnapshotArrows(IReadOnlyList<IReadOnlyList<Cell>> arrows)
    {
        if (arrows == null)
        {
            boardSnapshot = null;
            return;
        }
        var bytes = BinarySnapshot.EncodeArrows(arrows);
        boardSnapshot = Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Computes solve elapsed time in seconds from event timestamps.
    /// Sets a checkpoint at start_solve, accumulates elapsed on session_leave / end_solve,
    /// resets checkpoint on session_rejoin. Time between session_leave and session_rejoin
    /// is excluded. Returns 0 if no start_solve event is found.
    /// If the event stream ends without a closing event (session_leave / end_solve),
    /// includes time up to the last recorded event (handles autosaves and force-quits).
    /// </summary>
    [JsonIgnore]
    public double ComputedSolveElapsed
    {
        get
        {
            double elapsed = 0.0;
            DateTime checkpoint = DateTime.MinValue;
            DateTime lastEventTime = DateTime.MinValue;
            bool paused = false;
            bool finished = false;

            foreach (var evt in events)
            {
                var ts = DateTime.Parse(evt.timestamp).ToUniversalTime();

                switch (evt.type)
                {
                    case ReplayEventType.StartSolve:
                        checkpoint = ts;
                        paused = false;
                        break;
                    case ReplayEventType.SessionRejoin:
                        if (!paused && checkpoint != DateTime.MinValue)
                        {
                            // Orphan rejoin: accumulate up to last event, skip the gap.
                            elapsed += (lastEventTime - checkpoint).TotalSeconds;
                        }
                        checkpoint = ts;
                        paused = false;
                        break;
                    case ReplayEventType.SessionLeave:
                        elapsed += (ts - checkpoint).TotalSeconds;
                        paused = true;
                        break;
                    case ReplayEventType.EndSolve:
                        if (!paused)
                            elapsed += (ts - checkpoint).TotalSeconds;
                        finished = true;
                        break;
                }
                lastEventTime = ts;
            }

            // Include time from an unterminated session (autosave or force-quit
            // without session_leave). Without this, resuming from an autosave
            // would lose all solve time from the current session.
            if (
                !paused
                && !finished
                && checkpoint != DateTime.MinValue
                && lastEventTime != DateTime.MinValue
            )
                elapsed += (lastEventTime - checkpoint).TotalSeconds;

            return elapsed;
        }
    }

    /// <summary>Serializes this instance to a JSON string.</summary>
    public string ToJson() => JsonConvert.SerializeObject(this);
}

/// <summary>
/// Converter for <see cref="ReplayData.boardSnapshot"/>. Reads either a v4
/// JSON array of cell arrays (legacy) or a v5 base64 string. Always writes
/// the v5 base64 form. Also accepts a v4 server-side gzip+base64 wrapper
/// (already a string) and passes it through unchanged — the existing
/// VerificationWorker / LeaderboardScreenController code paths still apply
/// for stored top-50 server replays.
/// </summary>
internal class BoardSnapshotConverter : JsonConverter
{
    public override bool CanConvert(Type objectType) => objectType == typeof(string);

    public override object ReadJson(
        JsonReader reader,
        Type objectType,
        object existingValue,
        JsonSerializer serializer
    )
    {
        if (reader.TokenType == JsonToken.Null)
            return null;

        if (reader.TokenType == JsonToken.String)
            return (string)reader.Value;

        if (reader.TokenType == JsonToken.StartArray)
        {
            // v4 legacy: List<List<Cell>>. Re-encode to base64 binary.
            var jArr = JArray.Load(reader);
            var arrows = new List<IReadOnlyList<Cell>>(jArr.Count);
            foreach (var arrowToken in jArr)
            {
                var cellArray = (JArray)arrowToken;
                var cells = new List<Cell>(cellArray.Count);
                foreach (var cellToken in cellArray)
                {
                    int x = cellToken["X"]?.Value<int>() ?? 0;
                    int y = cellToken["Y"]?.Value<int>() ?? 0;
                    cells.Add(new Cell(x, y));
                }
                arrows.Add(cells);
            }
            var bytes = BinarySnapshot.EncodeArrows(arrows);
            return Convert.ToBase64String(bytes);
        }

        throw new JsonSerializationException(
            $"Unexpected token {reader.TokenType} for boardSnapshot."
        );
    }

    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    {
        writer.WriteValue((string)value);
    }
}
