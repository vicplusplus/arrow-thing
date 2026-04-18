using System;
using UnityEngine;

/// <summary>
/// One entry in <see cref="CoopSession.Roster"/>. Immutable — updated
/// with <see cref="With"/> and re-inserted into the dictionary.
/// </summary>
public readonly struct CoopPlayer : IEquatable<CoopPlayer>
{
    public Guid Id { get; }
    public string DisplayName { get; }
    public Color32 Color { get; }
    public int ClearCount { get; }
    public long AccumulatedMillis { get; }
    public bool Online { get; }
    public bool IsLocal { get; }

    public CoopPlayer(
        Guid id,
        string displayName,
        Color32 color,
        int clearCount,
        long accumulatedMillis,
        bool online,
        bool isLocal
    )
    {
        Id = id;
        DisplayName = displayName ?? "";
        Color = color;
        ClearCount = clearCount;
        AccumulatedMillis = accumulatedMillis;
        Online = online;
        IsLocal = isLocal;
    }

    /// <summary>Copy with selected field overrides.</summary>
    public CoopPlayer With(
        string displayName = null,
        Color32? color = null,
        int? clearCount = null,
        long? accumulatedMillis = null,
        bool? online = null
    ) =>
        new CoopPlayer(
            Id,
            displayName ?? DisplayName,
            color ?? Color,
            clearCount ?? ClearCount,
            accumulatedMillis ?? AccumulatedMillis,
            online ?? Online,
            IsLocal
        );

    public bool Equals(CoopPlayer other) =>
        Id == other.Id
        && DisplayName == other.DisplayName
        && Color.r == other.Color.r
        && Color.g == other.Color.g
        && Color.b == other.Color.b
        && Color.a == other.Color.a
        && ClearCount == other.ClearCount
        && AccumulatedMillis == other.AccumulatedMillis
        && Online == other.Online
        && IsLocal == other.IsLocal;

    public override bool Equals(object obj) => obj is CoopPlayer p && Equals(p);

    public override int GetHashCode() => Id.GetHashCode();
}
