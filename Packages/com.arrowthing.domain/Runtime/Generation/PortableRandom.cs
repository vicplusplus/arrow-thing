/// <summary>
/// Value-type xorshift32 RNG. Portable across Unity (Burst-compatible) and
/// .NET server builds. No dependencies on Unity.Mathematics or System.Random.
///
/// Same algorithm used by Unity.Mathematics.Random so that a given seed
/// produces the same sequence on both platforms (useful for replay
/// verification on the server).
/// </summary>
public struct PortableRandom
{
    public uint State;

    public PortableRandom(uint seed)
    {
        // Avoid state=0, which is an xorshift fixed point.
        State = seed == 0 ? 0x9E3779B9u : seed;
        NextState();
    }

    private uint NextState()
    {
        uint t = State;
        State ^= State << 13;
        State ^= State >> 17;
        State ^= State << 5;
        return t;
    }

    /// <summary>Returns an int in [min, max).</summary>
    public int NextInt(int min, int max)
    {
        uint range = (uint)(max - min);
        return (int)(NextState() % range) + min;
    }

    /// <summary>Returns an int in [0, max).</summary>
    public int NextInt(int max)
    {
        return (int)(NextState() % (uint)max);
    }
}
