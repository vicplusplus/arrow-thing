using System;
using System.Collections.Generic;
using Newtonsoft.Json;

/// <summary>
/// Serializable payload for one endless run. Sent to the server for
/// leaderboard submission + verification. Pure C# / Newtonsoft so the
/// same type can deserialize on both client (Unity Mono) and server
/// (.NET) without re-mapping.
///
/// Schema version history:
///   v1 — initial.
/// </summary>
public sealed class EndlessReplayData
{
    /// <summary>Format version — increment if the schema changes incompatibly.</summary>
    public int version = 1;

    /// <summary>UUID string. Uniquely identifies this run for idempotency.</summary>
    public string gameId;

    /// <summary>Application version at the time of recording.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string gameVersion;

    /// <summary>Spawn-RNG seed. Replay uses this to reproduce arrow generation.</summary>
    public int seed;

    /// <summary>Board side length. Endless boards are always square.</summary>
    public int boardSize;

    /// <summary>
    /// Tunings version — bumped whenever tuning constants change in a
    /// way that affects replay outcome. Verifier uses this to pick the
    /// right tuning table; mismatched versions are rejected up-front.
    /// v1 = initial endless tuning shipped with PR #145.
    /// </summary>
    public int tuningsVersion = 1;

    /// <summary>Ordered tap + topout events.</summary>
    public List<EndlessReplayEvent> events = new();

    /// <summary>Player-claimed final stats. Server re-computes and compares.</summary>
    public int claimedClears;
    public int claimedLongestCombo;
    public float claimedDurationSeconds;
}
