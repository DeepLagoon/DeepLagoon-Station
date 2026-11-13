using System.IO;
using System.Linq;
using System.Text.Json;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Microsoft.Data.Sqlite;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

/// <summary>Persistent sector manifests, weather epochs and survey expiry times.</summary>
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
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();

            using var transaction = _connection.BeginTransaction(deferred: false);
            command.Transaction = transaction;
            command.CommandText =
                "CREATE TABLE IF NOT EXISTS sector_manifests(campaign TEXT NOT NULL,id TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(campaign,id)); " +
                "CREATE TABLE IF NOT EXISTS sector_weather(campaign TEXT NOT NULL,sector TEXT NOT NULL,epoch INTEGER NOT NULL,PRIMARY KEY(campaign,sector)); " +
                "CREATE TABLE IF NOT EXISTS sector_surveys(campaign TEXT NOT NULL,sector TEXT NOT NULL,region TEXT NOT NULL,expires INTEGER NOT NULL,PRIMARY KEY(campaign,sector,region)); " +
                "CREATE TABLE IF NOT EXISTS sector_derelicts(campaign TEXT NOT NULL,sector TEXT NOT NULL,region TEXT NOT NULL,slot INTEGER NOT NULL CHECK(slot BETWEEN 0 AND 2),PRIMARY KEY(campaign,sector,region,slot)); " +
                "CREATE TABLE IF NOT EXISTS sector_encounters(campaign TEXT NOT NULL,sector TEXT NOT NULL,site TEXT NOT NULL,choice TEXT NOT NULL,PRIMARY KEY(campaign,sector,site));";
            command.ExecuteNonQuery();

            command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('sector_surveys') WHERE name='expires')";
            if ((long) command.ExecuteScalar()! == 0)
            {
                // Old surveys have no timestamps. Preserve their rows without making them active again.
                command.CommandText = "ALTER TABLE sector_surveys ADD COLUMN expires INTEGER NOT NULL DEFAULT 0";
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
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

    /// <summary>Compare-and-swap an append-only upgrade without rewriting bodies or the survey ledger.</summary>
    public SectorDefinition AppendRegions(string campaign, SectorDefinition expected, List<SpaceRegion> additions)
    {
        ValidateKey(campaign);
        Validate(expected);
        var expanded = new SectorDefinition
        {
            Version = expected.Version, Id = expected.Id, Seed = expected.Seed, Profile = expected.Profile,
            Star = expected.Star, Planets = new(expected.Planets), Regions = new(expected.Regions),
        };
        expanded.Regions.AddRange(additions);
        Validate(expanded);
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE sector_manifests SET data=$data WHERE campaign=$campaign AND id=$id AND data=$expected";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$expected", JsonSerializer.Serialize(expected, Json));
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(expanded, Json));
        command.ExecuteNonQuery();
        return Load(campaign, expected.Id) ?? throw new InvalidDataException("Missing sector during expansion.");
    }

    public long WeatherEpoch(string campaign, string sector, long now)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO sector_weather VALUES($campaign,$sector,$now) ON CONFLICT DO NOTHING; " +
            "SELECT epoch FROM sector_weather WHERE campaign=$campaign AND sector=$sector";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$now", now);
        return (long) command.ExecuteScalar()!;
    }

    public List<string> Surveys(string campaign, string sector, DateTimeOffset now, out long nextExpiry)
    {
        ValidateKey(campaign);
        ValidateKey(sector);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT region,expires FROM sector_surveys WHERE campaign=$campaign AND sector=$sector AND expires>$now ORDER BY region";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        nextExpiry = long.MaxValue;
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
            nextExpiry = Math.Min(nextExpiry, reader.GetInt64(1));
        }
        return result;
    }

    public bool TrySurvey(string campaign, string sector, string region, DateTimeOffset now)
    {
        var definition = Load(campaign, sector);
        if (definition == null || !definition.Regions.Any(r => r.Id == region)) return false;
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO sector_surveys(campaign,sector,region,expires) VALUES($campaign,$sector,$region,$expires) " +
            "ON CONFLICT(campaign,sector,region) DO UPDATE SET expires=excluded.expires WHERE sector_surveys.expires<=$now";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$region", region);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expires", now.AddHours(5).ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() == 1;
    }

    public int DerelictSlots(string campaign, string sector, string region)
    {
        ValidateKey(campaign);
        ValidateKey(sector);
        ValidateKey(region);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT slot FROM sector_derelicts WHERE campaign=$campaign AND sector=$sector AND region=$region";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$region", region);
        using var reader = command.ExecuteReader();
        var mask = 0;
        while (reader.Read()) mask |= 1 << reader.GetInt32(0);
        return mask;
    }

    public bool TryRecordDerelict(string campaign, string sector, string region, int slot)
    {
        if (slot is < 0 or > 2) return false;
        var definition = Load(campaign, sector);
        if (definition == null || slot >= OrbitalDebrisSystem.DerelictLimit(definition.Seed, region)
            || !definition.Regions.Any(r => r.Id == region && r.Kind == SpaceRegionKind.Asteroids)) return false;
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO sector_derelicts VALUES($campaign,$sector,$region,$slot) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$region", region);
        command.Parameters.AddWithValue("$slot", slot);
        return command.ExecuteNonQuery() == 1;
    }

    public static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("Invalid sector/campaign identity.");
    }

    public HashSet<string> Encounters(string campaign, string sector)
    {
        ValidateKey(campaign);
        ValidateKey(sector);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT site FROM sector_encounters WHERE campaign=$campaign AND sector=$sector";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        using var reader = command.ExecuteReader();
        var sites = new HashSet<string>();
        while (reader.Read()) sites.Add(reader.GetString(0));
        return sites;
    }

    public bool TryRecordEncounter(string campaign, string sector, string site, string choice)
    {
        if (choice is not ("data" or "cargo")) return false;
        var definition = Load(campaign, sector);
        if (definition == null || !NavigationSiteCatalog.Generate(definition).Any(s => s.Id == site && NavigationSiteCatalog.Available(definition, s))) return false;
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO sector_encounters VALUES($campaign,$sector,$site,$choice) ON CONFLICT DO NOTHING";
        command.Parameters.AddWithValue("$campaign", campaign);
        command.Parameters.AddWithValue("$sector", sector);
        command.Parameters.AddWithValue("$site", site);
        command.Parameters.AddWithValue("$choice", choice);
        return command.ExecuteNonQuery() == 1;
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
