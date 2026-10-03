using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Connection;
using Content.Server.EUI;
using Content.Server.Players.JobWhitelist;
using Content.Shared.CCVar;
using Robust.Server.Player;
using Robust.Server.ServerStatus;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Enums;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.DiscordLink;

public sealed class DiscordLinkSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _players = default!;
    [Dependency] private readonly EuiManager _euis = default!;
    [Dependency] private readonly IConfigurationManager _config = default!;
    [Dependency] private readonly IResourceManager _resources = default!;
    [Dependency] private readonly IStatusHost _status = default!;
    [Dependency] private readonly ITaskManager _tasks = default!;
    [Dependency] private readonly IServerDbManager _database = default!;
    [Dependency] private readonly JobWhitelistManager _whitelist = default!;
    [Dependency] private readonly IConnectionManager _connections = default!;
    private readonly HashSet<ICommonSession> _admitted = new();
    private DiscordLinkStore? _store;
    private string _token = "";
    private readonly bool _developmentBuild = CCVars.DiscordAdmissionDevelopment;
    public bool AdmissionRequired => !_developmentBuild && _config.GetCVar(CCVars.DiscordLinkEnabled);
    private bool _enabled;
    private bool _checkingPrompts;
    private DateTime _nextPromptCheck;
    private readonly Dictionary<ICommonSession, DiscordLinkEui> _prompts = new();
    private readonly System.Threading.SemaphoreSlim _apiLock = new(1, 1);

    public override void Initialize()
    {
        base.Initialize();
        _config.OnValueChanged(CCVars.DiscordLinkToken, UpdateToken, true);
        _config.OnValueChanged(CCVars.DiscordLinkEnabled, UpdateEnabled, true);
        _players.PlayerStatusChanged += OnStatusChanged;
        _status.AddHandler(HandleApi);
    }

    public static bool AdmissionAllowed(bool enabled, bool authenticated, bool linked, bool approved)
        => !enabled || (authenticated && linked && approved);

    public bool CanEnterRound(ICommonSession session)
        => AdmissionAllowed(AdmissionRequired,
            session.Channel.AuthType == LoginType.LoggedIn,
            _enabled && _store != null && _store.IsLinked(session.UserId.UserId),
            _admitted.Contains(session));

    public string AdmissionMessage(ICommonSession session)
    {
        if (session.Channel.AuthType != LoginType.LoggedIn)
            return "Для привязки и допуска войдите в авторизованный аккаунт SS14.";
        if (_store == null || !_store.IsLinked(session.UserId.UserId))
            return "Сначала привяжите Discord через игровое окно и канал привязки. До привязки вход в раунд и наблюдение недоступны.";
        return "Discord привязан. Создайте WL-заявку в Discord и дождитесь одобрения регистраторов. До допуска лобби и игра недоступны.";
    }

    public async Task RefreshAdmission(ICommonSession session)
    {
        if (!AdmissionRequired || !_enabled || _store == null ||
            session.Channel.AuthType != LoginType.LoggedIn || !_store.IsLinked(session.UserId.UserId))
        {
            _admitted.Remove(session);
            return;
        }
        try
        {
            var approved = await _connections.CheckDiscordLobbyWhitelist(session.Channel.UserData);
            if (approved && session.Status == SessionStatus.InGame)
            {
                var newlyAdmitted = _admitted.Add(session);
                if (_prompts.Remove(session, out var prompt))
                    prompt.Close();
                if (newlyAdmitted)
                    EntityManager.System<Content.Server.GameTicking.GameTicker>().CompleteDiscordAdmission(session);
            }
            else
                _admitted.Remove(session);
        }
        catch (Exception e)
        {
            Log.Error($"Discord lobby admission failed: {e.GetType().Name}");
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_enabled || _checkingPrompts || _prompts.Count == 0 || DateTime.UtcNow < _nextPromptCheck)
            return;
        _nextPromptCheck = DateTime.UtcNow.AddSeconds(3);
        RefreshPrompts();
    }

    private async void RefreshPrompts()
    {
        _checkingPrompts = true;
        try
        {
            foreach (var session in _prompts.Keys.ToArray())
                await RefreshAdmission(session);
        }
        finally
        {
            _checkingPrompts = false;
        }
    }

    private async Task RefreshUid(Guid uid)
    {
        foreach (var session in _players.Sessions.Where(s => s.UserId.UserId == uid).ToArray())
            await RefreshAdmission(session);
    }

    private void UpdateToken(string token) => _token = token;
    private void UpdateEnabled(bool enabled)
    {
        enabled &= !_developmentBuild;
        _enabled = enabled;
        _admitted.Clear();
        if (!enabled || _store != null)
            return;
        var root = _resources.UserData.RootDir;
        if (root == null)
        {
            _enabled = false;
            Log.Error("Discord linking requires a persistent server data directory.");
            return;
        }
        _store = new DiscordLinkStore(Path.Combine(root, "discord-links.db"));
    }

    public override void Shutdown()
    {
        _enabled = false;
        _players.PlayerStatusChanged -= OnStatusChanged;
        _config.UnsubValueChanged(CCVars.DiscordLinkToken, UpdateToken);
        _config.UnsubValueChanged(CCVars.DiscordLinkEnabled, UpdateEnabled);
        _store?.Dispose();
        _store = null;
        base.Shutdown();
    }

    private void OnStatusChanged(object? sender, SessionStatusEventArgs args)
    {
        if (args.NewStatus is SessionStatus.Disconnected or SessionStatus.Zombie)
        {
            _prompts.Remove(args.Session);
            _admitted.Remove(args.Session);
            return;
        }
        if (!_enabled || _store == null || args.NewStatus != SessionStatus.InGame ||
            args.Session.Channel.AuthType != LoginType.LoggedIn || _prompts.ContainsKey(args.Session) ||
            CanEnterRound(args.Session))
            return;
        var prompt = new DiscordLinkEui(_store, () => CanEnterRound(args.Session));
        _prompts.Add(args.Session, prompt);
        _euis.OpenEui(prompt, args.Session);
    }

    // Only the trusted bot process may redeem codes or grant whitelist. Keep this API on loopback.
    private async Task<bool> HandleApi(IStatusHandlerContext context)
    {
        var path = context.Url.AbsolutePath;
        if (!path.StartsWith("/deeplagoon/discord/", StringComparison.Ordinal))
            return false;
        context.ResponseHeaders["Cache-Control"] = "no-store";
        if (context.RequestMethod != HttpMethod.Post ||
            path is not ("/deeplagoon/discord/link" or "/deeplagoon/discord/lookup" or "/deeplagoon/discord/whitelist" or "/deeplagoon/discord/remove_whitelist"))
        {
            await context.RespondErrorAsync(HttpStatusCode.NotFound);
            return true;
        }
        var authorized = _enabled && _token.Length >= 32 &&
            IPAddress.IsLoopback(context.RemoteEndPoint.Address) &&
            context.RequestHeaders.TryGetValue("Authorization", out var auth) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth.ToString()), Encoding.UTF8.GetBytes("Bearer " + _token));
        if (!authorized)
        {
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }
        ApiRequest? request;
        try
        {
            var buffer = new byte[4097];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await context.RequestBody.ReadAsync(buffer.AsMemory(count));
                if (read == 0)
                    break;
                count += read;
            }
            request = count > 4096 ? null : JsonSerializer.Deserialize<ApiRequest>(buffer.AsSpan(0, count));
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request == null || !ulong.TryParse(request.DiscordId, out var id) || id == 0 ||
            request.DiscordId.Length is < 15 or > 20 || request.DiscordId.Any(c => !char.IsAsciiDigit(c)))
        {
            await context.RespondJsonAsync(new { error = "invalid_request" }, HttpStatusCode.BadRequest);
            return true;
        }
        await _apiLock.WaitAsync();
        try
        {
            var result = await OnMainThread(async () =>
            {
                if (!_enabled || _store == null)
                    return new ApiResult(HttpStatusCode.ServiceUnavailable, new { error = "unavailable" });
                try
                {
                    DiscordLinkStore.Link? link;
                    if (path.EndsWith("/link", StringComparison.Ordinal))
                    {
                        var code = request.Code?.Trim().ToUpperInvariant() ?? "";
                        if (code.Length != 24 || code.Any(c => !char.IsAsciiHexDigit(c)))
                            return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_code" });
                        link = _store.Consume(request.DiscordId, code);
                    }
                    else
                        link = _store.FindDiscord(request.DiscordId);
                    if (link == null)
                        return new ApiResult(HttpStatusCode.NotFound, new { error = "not_linked" });
                    var existing = false;
                    if (path.EndsWith("/whitelist", StringComparison.Ordinal))
                    {
                        var uid = new NetUserId(link.Uid);
                        existing = await _database.GetWhitelistStatusAsync(uid);
                        if (!existing)
                            await _whitelist.AddGlobalWhitelistAsync(uid);
                        if (!await _database.GetWhitelistStatusAsync(uid))
                            throw new InvalidOperationException("Whitelist write not confirmed");
                    }
                    if (path.EndsWith("/remove_whitelist", StringComparison.Ordinal))
                    {
                        var uid = new NetUserId(link.Uid);
                        // Only the trusted bot can assert the initiating member has the host role.
                        if (!request.HostAuthorized && await _database.GetAdminDataForAsync(uid) != null)
                            return new ApiResult(HttpStatusCode.Forbidden, new { error = "admin_protected" });
                        existing = await _database.GetWhitelistStatusAsync(uid);
                        if (existing)
                            await _whitelist.RemoveGlobalWhitelistAsync(uid);
                        if (await _database.GetWhitelistStatusAsync(uid))
                            throw new InvalidOperationException("Whitelist removal not confirmed");
                    }
                    await RefreshUid(link.Uid);
                    return new ApiResult(HttpStatusCode.OK, new { uid = link.Uid, username = link.Username, existing });
                }
                catch (DiscordLinkStore.LinkException e)
                {
                    return new ApiResult(e.Message == "rate_limited" ? HttpStatusCode.TooManyRequests : HttpStatusCode.Conflict,
                        new { error = e.Message });
                }
            });
            await context.RespondJsonAsync(result.Body, result.Status);
        }
        catch (Exception e)
        {
            // Never log codes, request bodies or tokens.
            Log.Error($"Discord link API failed: {e.GetType().Name}");
            await context.RespondJsonAsync(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            _apiLock.Release();
        }
        return true;
    }

    private Task<ApiResult> OnMainThread(Func<Task<ApiResult>> action)
    {
        var completion = new TaskCompletionSource<ApiResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(async () =>
        {
            try { completion.TrySetResult(await action()); }
            catch (Exception e) { completion.TrySetException(e); }
        });
        return completion.Task;
    }

    private sealed record ApiRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("discord_id")] string DiscordId,
        [property: System.Text.Json.Serialization.JsonPropertyName("code")] string? Code,
        [property: System.Text.Json.Serialization.JsonPropertyName("host_authorized")] bool HostAuthorized = false);
    private sealed record ApiResult(HttpStatusCode Status, object Body);
}
