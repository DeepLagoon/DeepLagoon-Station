using System.IO;
using System.Linq;
using System.Text.Json;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

/// <summary>Immutable sector manifests and an idempotent survey ledger keyed by stable world and sector identities.</summary>
public sealed class SectorStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private readonly SqliteConnection _connection;

    public SectorStore(string path)
    {
        if (path != ":memory:") Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
#if USE_SYSTEM_SQLITE
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
#endif
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; " +
            "CREATE TABLE IF NOT EXISTS sector_manifests(campaign TEXT NOT NULL,id TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(campaign,id)); " +
            "CREATE TABLE IF NOT EXISTS sector_surveys(campaign TEXT NOT NULL,sector TEXT NOT NULL,region TEXT NOT NULL,PRIMARY KEY(campaign,sector,region));";
        command.ExecuteNonQuery();
    }

    public SectorDefinition? Load(string campaign, string id)
    {
        ValidateKey(campaign);
        ValidateKey(id);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT data FROM sector_manifests WHERE campaign=$campaign AND id=$id";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteScalar() is not string json) return null;
        if (json.Length > 262144) throw new InvalidDataException("Sector manifest exceeds the supported size.");
        var sector = JsonSerializer.Deserialize<SectorDefinition>(json, Json)
            ?? throw new InvalidDataException("Empty sector manifest.");
        Validate(sector);
        if (sector.Id != id) throw new InvalidDataException("Sector key differs from manifest identity.");
        return sector;
    }

    public SectorDefinition GetOrCreate(string campaign, SectorDefinition sector)
    {
        ValidateKey(campaign);
        Validate(sector);
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO sector_manifests VALUES($campaign,$id,$data) ON CONFLICT(campaign,id) DO NOTHING";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$id", sector.Id);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(sector, Json));
        command.ExecuteNonQuery();
        return Load(campaign, sector.Id)!;
    }

    public List<string> Surveys(string campaign, string sector)
    {
        ValidateKey(campaign);
        ValidateKey(sector);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT region FROM sector_surveys WHERE campaign=$campaign AND sector=$sector ORDER BY region";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public bool TrySurvey(string campaign, string sector, string region)
    {
        var definition = Load(campaign, sector);
        if (definition == null || !definition.Regions.Any(r => r.Id == region)) return false;
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO sector_surveys VALUES($campaign,$sector,$region) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$region", region);
        return command.ExecuteNonQuery() == 1;
    }

    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("Invalid sector/campaign identity.");
    }

    public static void Validate(SectorDefinition sector)
    {
        ValidateKey(sector.Id);
        if (string.IsNullOrWhiteSpace(sector.Profile) || string.IsNullOrWhiteSpace(sector.Star))
            throw new InvalidDataException("Missing sector star/profile.");
        if (sector.Version != SectorDefinition.CurrentVersion)
            throw new InvalidDataException($"Unsupported sector manifest version {sector.Version}; refusing to regenerate it.");
        if (sector.Planets == null || sector.Regions == null || sector.Planets.Count > SectorGenerator.MaxPlanets
            || sector.Regions.Count > SectorGenerator.MaxRegions) throw new InvalidDataException("Invalid sector object count.");
        var ids = new HashSet<string>();
        foreach (var planet in sector.Planets)
        {
            if (planet == null || !ids.Add(planet.Id) || string.IsNullOrWhiteSpace(planet.Id) || string.IsNullOrWhiteSpace(planet.Template)
                || !Coordinate(planet.X) || !Coordinate(planet.Y)) throw new InvalidDataException("Invalid sector planet.");
        }
        ids.Clear();
        foreach (var region in sector.Regions)
        {
            if (region == null || !ids.Add(region.Id) || string.IsNullOrWhiteSpace(region.Id) || !Enum.IsDefined(region.Kind)
                || !Coordinate(region.X) || !Coordinate(region.Y) || !float.IsFinite(region.Radius)
                || region.Radius is < 100f or > 2000f || !float.IsFinite(region.Aspect) || region.Aspect is < 0.25f or > 1f
                || !float.IsFinite(region.Rotation) || !float.IsFinite(region.Intensity) || region.Intensity is < 0f or > 1f)
                throw new InvalidDataException("Invalid sector region.");
        }
    }

    private static bool Coordinate(float value) => float.IsFinite(value) && MathF.Abs(value) <= 100000f;
    public void Dispose() => _connection.Dispose();
}
