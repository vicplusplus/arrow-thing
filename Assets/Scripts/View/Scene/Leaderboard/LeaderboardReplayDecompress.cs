using System;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Server-side replay payloads carry their <c>boardSnapshot</c> as a
/// gzip-base64 string for top-50 entries (saves a few hundred KB per
/// row). Client deserializers expect the snapshot as a nested JSON
/// array, so this helper transparently expands the string-shaped
/// snapshot back to its array form before <c>Newtonsoft.Json</c>
/// reaches the <c>ReplayData.boardSnapshot</c> field.
///
/// <para>Pure string transform — fail-soft: on any parse / decompression
/// error, returns the original JSON unchanged and logs a warning. The
/// downstream <c>ReplayData</c> deserialize will then surface the real
/// error if the payload is genuinely malformed.</para>
/// </summary>
public static class LeaderboardReplayDecompress
{
    /// <summary>
    /// Returns <paramref name="replayJson"/> with any string-shaped
    /// <c>boardSnapshot</c> field replaced by its decompressed JSON
    /// array. Snapshots already in array form pass through unchanged.
    /// </summary>
    public static string DecompressSnapshotIfNeeded(string replayJson)
    {
        try
        {
            var obj = JObject.Parse(replayJson);
            var snapshot = obj["boardSnapshot"];
            if (snapshot == null || snapshot.Type != JTokenType.String)
                return replayJson;

            var base64 = (string)snapshot;
            var compressed = Convert.FromBase64String(base64);
            using var ms = new MemoryStream(compressed);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            var snapshotJson = reader.ReadToEnd();

            obj["boardSnapshot"] = JToken.Parse(snapshotJson);
            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception e)
        {
            Debug.LogWarning(
                $"[LeaderboardReplayDecompress] Snapshot decompression failed: {e.Message}"
            );
            return replayJson;
        }
    }
}
