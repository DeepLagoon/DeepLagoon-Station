using System.Linq;
using System.Numerics;
namespace Content.Shared._DeepLagoon.Voyages;

/// <summary>Stable, bounded map generation; no entities or random client-side outcomes.</summary>
public static class VoyageGalaxy
{
    public const int Rings = 8, PerRing = 12;
    public const float Radius = 2250;
    public static uint Hash(string text, int seed = 0)
    {
        var value = 2166136261u ^ (uint)seed;
        foreach (var c in text) value = (value ^ c) * 16777619u;
        return value;
    }
    public static string LaneKey(string from, string to) => string.CompareOrdinal(from, to) < 0 ? from + ":" + to : to + ":" + from;
    public static double PriceFactor(Vector2 position) => 1 - .45 * Math.Clamp(position.Length() / Radius, 0, 1);
    public static void Extend(List<VoyageDestination> systems, List<VoyageLane> lanes, int seed)
    {
        string[] names = ["Альтаир", "Вега", "Астерия", "Таласса", "Арктур", "Мираж", "Кассиопея", "Персей", "Ирида", "Сириус", "Орион", "Селена"];
        string[] types = ["PlanetTypeBarren", "PlanetTypeIce", "PlanetTypeDesert", "PlanetTypeGasGiant", "PlanetTypeVolcanic", "PlanetTypeHabitable"];
        string[] roles = ["Горнодобывающие колонии", "Промышленные верфи", "Медицинские колонии", "Исследовательский анклав", "Аграрные миры", "Оружейный рынок"];
        string[] categories = ["materials", "engineering", "medical", "science", "food", "security"];
        string Id(int ring, int slot) => "sector-" + ring + "-" + (slot % PerRing + PerRing) % PerRing;
        void Link(string a, string b, bool charted = false)
        {
            if (a == b || lanes.Any(l => LaneKey(l.From, l.To) == LaneKey(a, b))) return;
            var start = systems.Single(s => s.Id == a); var end = systems.Single(s => s.Id == b);
            var minutes = Math.Max(8, (int)(Vector2.Distance(start.Position, end.Position) / 24) + (int)(Hash(a + b, seed) % 7));
            lanes.Add(new(a, b, minutes, charted));
        }
        for (var ring = 0; ring < Rings; ring++)
            for (var slot = 0; slot < PerRing; slot++)
            {
                var id = Id(ring, slot); var hash = Hash(id, seed);
                // Irregular northern spiral fan. Stable IDs/links keep saved voyages valid.
                var angleNoise = Hash(id + "/angle", seed) / (float)uint.MaxValue - .5f;
                var radiusNoise = Hash(id + "/radius", seed) / (float)uint.MaxValue - .5f;
                var angle = .18f + (slot + .5f) * 2.75f / PerRing + angleNoise * .25f + .12f * MathF.Sin(ring * .8f + slot * .7f);
                var radius = 550 + ring * 250 + radiusNoise * 290 + 100 * MathF.Sin(slot * 1.7f + ring * .5f);
                var x = MathF.Cos(angle) * radius * 1.16f;
                var y = 230 + MathF.Sin(angle) * radius;
                var kind = (int)(hash % 6);
                systems.Add(new(id, names[(slot + (int)(hash % 12)) % 12] + " " + (ring + 1) + "-" + (slot + 1),
                    Place(systems, new Vector2(x, y), id, seed, north: true), types[kind], roles[kind], false, [], categories[kind]));
            }
        string[] ports = ["Металлургическая фактория", "Инженерный порт", "Медицинская станция", "Научный узел", "Аграрная биржа", "Оружейный причал",
            "Дальние плавильни", "Ремонтный док", "Клинический склад", "Исследовательский рынок", "Продовольственный порт", "Военный аванпост"];
        string[][] goods = [["SheetSteel", "SheetGlass"], ["CableApcStack", "PowerCellMedium"], ["Brutepack", "Ointment"],
            ["RogueSiliconResearchDisk50000"], ["FoodPizzaMargherita"], ["ClothingOuterArmorBasic"]];
        for (var i = 0; i < ports.Length; i++)
        {
            var id = "trade-" + i; var row = i / 4; var column = i % 4;
            systems.Add(new(id, ports[i], Place(systems, new Vector2((column - 1.5f) * 650 + (Hash(id + "/x", seed) % 321) - 160, -550 - row * 460 + (Hash(id + "/y", seed) % 181) - 90), id, seed, north: false), types[i % types.Length], "Безопасный торговый маршрут", true, [], categories[i % categories.Length], goods[i % goods.Length]));
            Link(i < 4 ? "colossus" : "trade-" + (i - 4), id, true);
        }
        Link("colossus", "centcomm", true);
        string[] anchors = ["boreas", "nereid", "frontier", "helios", "echo", "forge"];
        for (var ring = 0; ring < Rings; ring++)
            for (var slot = 0; slot < PerRing; slot++)
            {
                var id = Id(ring, slot);
                if (ring == 0) Link(anchors[slot * anchors.Length / PerRing], id);
                else Link(Id(ring - 1, slot), id);
                Link(id, Id(ring, slot + 1));
                if (ring > 0 && slot % 3 == 0) Link(Id(ring - 1, slot + 1), id);
            }
    }
    private static Vector2 Place(IReadOnlyList<VoyageDestination> systems, Vector2 desired, string id, int seed, bool north)
    {
        const float spacing = 260;
        var phase = Hash(id + "/placement", seed) / (float)uint.MaxValue * MathF.Tau;
        for (var attempt = 0; attempt < 1024; attempt++)
        {
            var radius = attempt == 0 ? 0 : 95 * MathF.Sqrt(attempt);
            var angle = phase + attempt * 2.3999632f;
            var point = desired + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            if (point.X < -3200 || point.X > 3200 || point.Y > 3250 || point.Y < -2100 || (north ? point.Y < 240 : point.Y > -250)) continue;
            if (systems.All(other => Vector2.DistanceSquared(point, other.Position) >= spacing * spacing)) return point;
        }
        throw new InvalidOperationException("Unable to place a bounded sector without overlapping another node.");
    }
    /// <summary>Clipped Voronoi borders: presentation geometry only, never physical maps or regions.</summary>
    public static Vector2[] SectorCell(IReadOnlyList<VoyageDestination> systems, string id)
    {
        var center = systems.First(s => s.Id == id).Position;
        var polygon = new List<Vector2> { new(-3400, -2400), new(3400, -2400), new(3400, 3400), new(-3400, 3400) };
        foreach (var other in systems)
        {
            if (other.Id == id) continue;
            var normal = other.Position - center;
            if (normal.LengthSquared() < .01f) continue;
            var threshold = (other.Position.LengthSquared() - center.LengthSquared()) * .5f;
            var result = new List<Vector2>();
            for (var i = 0; i < polygon.Count; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                var da = Vector2.Dot(a, normal) - threshold; var db = Vector2.Dot(b, normal) - threshold;
                if (da <= 0) result.Add(a);
                if ((da <= 0) != (db <= 0)) result.Add(Vector2.Lerp(a, b, da / (da - db)));
            }
            polygon = result; if (polygon.Count == 0) break;
        }
        return polygon.ToArray();
    }

}
