using System.Numerics;
using Content.Shared.Damage;
using Robust.Shared.Map;

namespace Content.Server._DeepLagoon.Bitrunning;

internal static class BitrunBossGeometry
{
    public static IEnumerable<EntityCoordinates> Square(EntityCoordinates center, int radius)
    {
        for (var x = -radius; x <= radius; x++)
        for (var y = -radius; y <= radius; y++) yield return center.Offset(new Vector2(x, y));
    }

    public static IEnumerable<EntityCoordinates> Ring(EntityCoordinates center, int radius)
    {
        for (var x = -radius; x <= radius; x++)
        for (var y = -radius; y <= radius; y++)
            if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius) yield return center.Offset(new Vector2(x, y));
    }

    public static float Chebyshev(MapCoordinates a, MapCoordinates b) =>
        a.MapId != b.MapId ? float.PositiveInfinity : MathF.Max(Math.Abs(a.Position.X - b.Position.X), Math.Abs(a.Position.Y - b.Position.Y));

    public static IEnumerable<EntityCoordinates> Line(EntityCoordinates origin, double angle, int range)
    {
        var x = (int)MathF.Floor(origin.Position.X); var y = (int)MathF.Floor(origin.Position.Y);
        var endX = x + (int)Math.Round(Math.Cos(angle) * range);
        var endY = y + (int)Math.Round(Math.Sin(angle) * range);
        var dx = Math.Abs(endX - x); var dy = -Math.Abs(endY - y);
        var sx = x < endX ? 1 : -1; var sy = y < endY ? 1 : -1;
        var error = dx + dy;
        while (x != endX || y != endY)
        {
            var twice = error * 2;
            if (twice >= dy) { error += dy; x += sx; }
            if (twice <= dx) { error += dx; y += sy; }
            yield return new EntityCoordinates(origin.EntityId, new Vector2(x + 0.5f, y + 0.5f));
        }
    }

    public static void Heal(DamageableSystem system, EntityUid uid, DamageableComponent component, float amount)
    {
        var total = component.TotalDamage.Float();
        if (total <= 0) return;
        var heal = new DamageSpecifier();
        foreach (var (type, damage) in component.Damage.DamageDict)
            if (damage > 0) heal.DamageDict[type] = -damage.Float() * MathF.Min(amount, total) / total;
        system.TryChangeDamage(uid, heal, true);
    }
}
