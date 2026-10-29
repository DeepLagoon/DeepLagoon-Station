namespace Content.Shared._DeepLagoon.SpaceEnvironment;

/// <summary>Versioned, platform independent streams. Never use string.GetHashCode or System.Random for manifests.</summary>
public struct SectorRandom
{
    private uint _state;
    public SectorRandom(uint seed) => _state = seed == 0 ? 0x6D2B79F5u : seed;

    public uint Next()
    {
        var value = _state;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return _state = value;
    }

    public float Unit() => (Next() >> 8) * (1f / 16777216f);
    public float Range(float min, float max) => min + Unit() * (max - min);
    public int Index(int count) => (int) (Next() % (uint) count);

    public static uint Derive(uint seed, string key)
    {
        var hash = seed ^ 2166136261u;
        foreach (var character in key)
            hash = unchecked((hash ^ character) * 16777619u);
        return hash == 0 ? 1u : hash;
    }
}
