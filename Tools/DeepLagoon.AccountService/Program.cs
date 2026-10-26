using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Server.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DeepLagoon.AccountService;

public static class Program
{
    public static async Task Main(string[] args)
    {
        SQLitePCL.Batteries_V2.Init();
        var settings = ServiceSettings.FromEnvironment();
        var app = await CreateApplication(settings, args);
        await app.RunAsync();
    }

    public static async Task<WebApplication> CreateApplication(ServiceSettings settings, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(args ?? []);
        // Connection errors and EF diagnostics can contain credentials or personal data.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new AccountOperations(settings));
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, settings.Port);
            k.Limits.MaxRequestBodySize = 4096;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
        });
        var app = builder.Build();
        var operations = app.Services.GetRequiredService<AccountOperations>();
        var slots = new SemaphoreSlim(16, 16);
        var healthSlots = new SemaphoreSlim(1, 1);
        app.MapGet("/health/live", () => Results.Json(new { alive = true, economy_api_version = 1 }));
        async Task<IResult> Readiness(HttpContext context)
        {
            using var lease = new AccountRequestLease(healthSlots);
            if (!lease.Acquired) return Results.Json(new { ready = false }, statusCode: 503);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            cancel.CancelAfter(TimeSpan.FromSeconds(3));
            try { await operations.CheckSchema(cancel.Token); return Results.Json(new { ready = true, economy_api_version = 1 }); }
            catch { return Results.Json(new { ready = false, economy_api_version = 1 }, statusCode: 503); }
        }
        app.MapGet("/health", (Func<HttpContext, Task<IResult>>) Readiness);
        app.MapGet("/health/ready", (Func<HttpContext, Task<IResult>>) Readiness);
        app.MapPost("/deeplagoon/discord/{action}", async (HttpContext context, string action) =>
        {
            if (context.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer))
                return Results.Unauthorized();
            var token = action switch
            {
                "whitelist" or "remove_whitelist" => settings.WhitelistToken,
                "lc_add" or "lc_remove" or "lc_history" => settings.CoinToken,
                _ when action.StartsWith("economy_", StringComparison.Ordinal) => settings.CoinToken,
                _ => null,
            };
            if (token == null) return Results.NotFound();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()),
                    Encoding.UTF8.GetBytes("Bearer " + token)))
                return Results.Unauthorized();
            using var requestLease = new AccountRequestLease(slots);
            if (!requestLease.Acquired) return Results.Json(new { error = "busy" }, statusCode: 429);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                if (action.StartsWith("economy_", StringComparison.Ordinal))
                {
                    var economy = await JsonSerializer.DeserializeAsync<EconomyRequest>(context.Request.Body,
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }, deadline.Token);
                    if (economy == null) return Results.BadRequest(new { error = "invalid_request" });
                    return Results.Json(await operations.ExecuteEconomy(action, economy, deadline.Token));
                }
                var request = await JsonSerializer.DeserializeAsync<AccountRequest>(context.Request.Body,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }, deadline.Token);
                if (request == null) return Results.BadRequest(new { error = "invalid_request" });
                return Results.Json(await operations.Execute(action, request, deadline.Token));
            }
            catch (AccountError e) { return Results.Json(new { error = e.Code }, statusCode: e.Status); }
            catch (InsufficientLagoonCoinsException)
            { return Results.Conflict(new { error = "insufficient_lc" }); }
            catch (Exception e) when (e is JsonException or BadHttpRequestException or ArgumentException or OverflowException)
            { return Results.BadRequest(new { error = "invalid_request" }); }
            catch (Exception e)
            {
                // Never emit request bodies, tokens, database paths or exception messages.
                Console.Error.WriteLine($"Account operation failed: {e.GetType().Name}");
                return Results.Json(new { error = "unavailable" }, statusCode: 503);
            }
        });
        return app;
    }
}

public sealed record ServiceSettings(string Engine, string Connection, string LinkDatabase,
    string ReceiptDatabase, string WhitelistToken, string CoinToken, int Port = 1213)
{
    public static ServiceSettings FromEnvironment()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing {name}");
        var settings = new ServiceSettings(Required("ACCOUNT_DB_ENGINE"), Required("ACCOUNT_DB_CONNECTION"),
            Required("ACCOUNT_LINK_DATABASE"), Required("ACCOUNT_RECEIPT_DATABASE"),
            Required("ACCOUNT_WHITELIST_TOKEN"), Required("ACCOUNT_COIN_TOKEN"),
            int.Parse(Environment.GetEnvironmentVariable("ACCOUNT_LISTEN_PORT") ?? "1213"));
        if (settings.Engine is not ("postgres" or "sqlite") || settings.Port is < 1024 or > 65535 ||
            settings.WhitelistToken.Length < 32 || settings.CoinToken.Length < 32 || settings.WhitelistToken == settings.CoinToken ||
            !Path.IsPathFullyQualified(settings.LinkDatabase) || !File.Exists(settings.LinkDatabase) ||
            !Path.IsPathFullyQualified(settings.ReceiptDatabase) || settings.LinkDatabase == settings.ReceiptDatabase)
            throw new InvalidOperationException("Invalid account service settings");
        return settings;
    }

    public ServerDbContext OpenDatabase() => Engine switch
    {
        "postgres" => new PostgresServerDbContext(new DbContextOptionsBuilder<PostgresServerDbContext>()
            .UseNpgsql(Connection, o => o.CommandTimeout(8)).Options),
        "sqlite" => new SqliteServerDbContext(new DbContextOptionsBuilder<SqliteServerDbContext>()
            .UseSqlite(Connection, o => o.CommandTimeout(8)).Options),
        _ => throw new InvalidOperationException("Unsupported database engine"),
    };
}

public sealed record AccountRequest(string DiscordId, string? ActorDiscordId = null, string? RequestId = null,
    long Amount = 0, string? Reason = null, bool HostAuthorized = false, int Page = 1);

public sealed class AccountError(string code, int status = 409) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed record IdentityLink(Guid Uid, string Username);

/// <summary>No game status requests, ECS, or game thread dependencies.</summary>
public sealed partial class AccountOperations(ServiceSettings settings) : IDisposable
{
    // Keep a read-only WAL connection alive independently of the game. Otherwise
    // the last game writer can remove WAL/SHM on shutdown, leaving a narrowly
    // privileged reader unable to recreate them in the protected directory.
    private SqliteConnection? _linkLease;
    private readonly object _linkLeaseLock = new();

    public void Dispose()
    {
        lock (_linkLeaseLock) { _linkLease?.Dispose(); _linkLease = null; }
    }

    private SqliteConnection EnsureLinkLease()
    {
        lock (_linkLeaseLock) return _linkLease ??= OpenLinks();
    }

    private SqliteConnection OpenLinks()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = settings.LinkDatabase, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3 }.ToString());
        db.Open();
        return db;
    }

    public async Task CheckSchema(CancellationToken cancel = default)
    {
        await using var db = settings.OpenDatabase();
        // Never create or migrate the game's schema from this narrowly privileged service.
        await db.Preference.Select(p => new { p.UserId, p.LagoonCoins, p.LagoonCoinPlayedTicks, p.LagoonCoinBonusTicks }).Take(1).ToListAsync(cancel);
        await db.LagoonCoinOperations.Take(1).ToListAsync(cancel);
        await db.LagoonEconomyRecords.Take(1).ToListAsync(cancel);
        await db.Whitelist.Take(1).ToListAsync(cancel);
        await db.Admin.Select(a => a.UserId).Take(1).ToListAsync(cancel);
        var links = EnsureLinkLease();
        using var command = links.CreateCommand();
        command.CommandText = "SELECT discord_id,ss14_uid,username FROM discord_links LIMIT 1; SELECT official_uid,canonical_uid,discord_id FROM discord_account_merge_pending LIMIT 1; SELECT official_uid,canonical_uid,discord_id FROM discord_account_aliases LIMIT 1";
        command.ExecuteNonQuery();
        using var receipts = OpenReceipts();
    }

    private IdentityLink? FindLink(string discord)
    {
        EnsureLinkLease();
        using var db = OpenLinks();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT ss14_uid,username FROM discord_links WHERE discord_id=$discord";
        command.Parameters.AddWithValue("$discord", discord);
        using var row = command.ExecuteReader();
        return row.Read() ? new IdentityLink(Guid.Parse(row.GetString(0)), row.GetString(1)) : null;
    }

    private void AssertStableLink(string discord, Guid expected)
    {
        using var db = OpenLinks();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM discord_links WHERE discord_id=$discord AND ss14_uid=$uid
            AND NOT EXISTS (SELECT 1 FROM discord_account_merge_pending p
                WHERE (p.official_uid=$uid OR p.canonical_uid=$uid)
                AND NOT EXISTS (SELECT 1 FROM discord_account_aliases a
                    WHERE a.official_uid=p.official_uid AND a.canonical_uid=p.canonical_uid AND a.discord_id=p.discord_id))
            """;
        command.Parameters.AddWithValue("$discord", discord);
        command.Parameters.AddWithValue("$uid", expected.ToString());
        if (command.ExecuteScalar() == null) throw new AccountError("merge_pending_or_stale_link");
    }

    private SqliteConnection OpenReceipts()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = settings.ReceiptDatabase, Pooling = false, DefaultTimeout = 3 }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS receipts(
                request_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, user_id TEXT NOT NULL, actor_id TEXT);
            """;
        command.ExecuteNonQuery();
        return db;
    }

    private Guid? BindReceipt(AccountRequest request, string action, IdentityLink target, Guid? actor)
    {
        // Bind an interaction ID globally before touching the wallet. Replays cannot
        // move the same receipt to a different account after a link changes.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { action, request.DiscordId, request.ActorDiscordId, request.Amount, reason = request.Reason!.Trim() })));
        using var db = OpenReceipts();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO receipts VALUES($id,$fingerprint,$uid,$actor) ON CONFLICT(request_id) DO NOTHING;
            SELECT fingerprint,user_id,actor_id FROM receipts WHERE request_id=$id;
            """;
        command.Parameters.AddWithValue("$id", request.RequestId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$uid", target.Uid.ToString());
        command.Parameters.AddWithValue("$actor", (object?)actor?.ToString() ?? DBNull.Value);
        using var row = command.ExecuteReader();
        if (!row.Read() || row.GetString(0) != fingerprint || Guid.Parse(row.GetString(1)) != target.Uid)
            throw new AccountError("operation_conflict");
        return row.IsDBNull(2) ? null : Guid.Parse(row.GetString(2));
    }

    private static bool DiscordId(string? id) => id is { Length: >= 15 and <= 20 } &&
        id.All(char.IsAsciiDigit) && ulong.TryParse(id, out var value) && value > 0;

    public async Task<object> Execute(string action, AccountRequest request, CancellationToken cancel = default)
    {
        var whitelist = action is "whitelist" or "remove_whitelist";
        var history = action == "lc_history";
        if (!whitelist && !history && action is not ("lc_add" or "lc_remove")) throw new AccountError("not_found", 404);
        if (!DiscordId(request.DiscordId) || (!whitelist &&
            (!request.HostAuthorized || !DiscordId(request.ActorDiscordId) || !DiscordId(request.RequestId) ||
             request.Page is < 1 or > 100000 || (!history &&
             (request.Amount <= 0 || request.Amount > 9007199254740991 || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 400)))))
            throw new AccountError("invalid_request", 400);
        var target = FindLink(request.DiscordId) ?? throw new AccountError("not_linked", 404);
        AssertStableLink(request.DiscordId, target.Uid);
        await using var db = settings.OpenDatabase();
        if (whitelist)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancel);
            if (action == "remove_whitelist" && !request.HostAuthorized &&
                await db.Admin.AnyAsync(a => a.UserId == target.Uid, cancel))
                throw new AccountError("admin_protected", 403);
            var existing = await db.Whitelist.AnyAsync(w => w.UserId == target.Uid, cancel);
            if (action == "whitelist")
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO whitelist (user_id) VALUES ({target.Uid}) ON CONFLICT (user_id) DO NOTHING", cancel);
            else
                await db.Whitelist.Where(w => w.UserId == target.Uid).ExecuteDeleteAsync(cancel);
            AssertStableLink(request.DiscordId, target.Uid);
            await transaction.CommitAsync(cancel);
            return new { uid = target.Uid, username = target.Username, existing };
        }
        if (history)
        {
            var rows = await AccountCurrencyOperations.GetLagoonCoinHistory(db, target.Uid, request.Page, cancel);
            var balance = await db.Preference.Where(p => p.UserId == target.Uid).Select(p => p.LagoonCoins).SingleAsync(cancel);
            return new { uid = target.Uid, request_id = request.RequestId, balance, has_more = rows.Count > 20,
                rows = rows.Take(20).Select(r => new { amount = r.Amount, reason = r.Reason, actor_uid = r.ActorId,
                    created_at = r.CreatedAt, operation_id = r.OperationId }) };
        }
        var author = FindLink(request.ActorDiscordId!);
        var actor = BindReceipt(request, action, target, author?.Uid);
        var result = await AccountCurrencyOperations.AwardLagoonCoins(db, target.Uid, $"discord:{request.RequestId}",
            action == "lc_remove" ? -request.Amount : request.Amount,
            $"Discord actor={request.ActorDiscordId}; {request.Reason!.Trim()}", actor,
            cancel: cancel, beforeCommit: () => AssertStableLink(request.DiscordId, target.Uid));
        return new { uid = target.Uid, balance = result.Balance, applied = result.Applied, request_id = request.RequestId };
    }
}
