namespace Content.Shared._DeepLagoon.SpaceEnvironment;

public static class PlasmaWeather
{
    public static uint Seed(uint sectorSeed, string region)
        => SectorRandom.Derive(sectorSeed, "sites-v1/region/" + region);

    public static double Phase(uint seed, double seconds) => (seconds + seed % 90) % 90;
    public static bool Quiet(uint seed, double seconds) => Phase(seed, seconds) >= 70;
    public static int QuietIn(uint seed, double seconds) => Quiet(seed, seconds) ? 0 : (int) Math.Ceiling(70 - Phase(seed, seconds));

    public static float Strength(uint seed, double seconds)
    {
        var phase = Phase(seed, seconds);
        if (phase >= 70)
            return 0.15f;

        return 0.8f + 0.2f * (0.5f + 0.5f * MathF.Cos((float) (phase / 7) * MathF.Tau));
    }
}
