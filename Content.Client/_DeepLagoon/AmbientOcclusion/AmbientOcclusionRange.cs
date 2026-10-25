namespace Content.Client._DeepLagoon.AmbientOcclusion;

internal static class AmbientOcclusionRange
{
    public const int MinDistance = 8;
    public const int MaxDistance = 32;
    public const int MinCasters = 256;
    public const int MaxCasters = 2048;

    public static float GetOpacity(float distance, float range)
    {
        var fadeWidth = Math.Min(4f, range * 0.2f);
        if (fadeWidth <= 0)
            return 0;

        var fade = Math.Clamp((range - distance) / fadeWidth, 0f, 1f);
        return fade * fade * (3f - 2f * fade);
    }

    public static float GetBudgetOpacity(int index, int limit)
    {
        // Fade the farthest quarter by sorted rank so an outlier leaving cannot move the fade boundary.
        var fade = Math.Clamp((limit - 1 - index) / (limit * 0.25f), 0f, 1f);
        return fade * fade * (3f - 2f * fade);
    }
}
