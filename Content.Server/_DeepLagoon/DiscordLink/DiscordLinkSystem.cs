using System.IO;
using Content.Shared._DeepLagoon.DiscordLink;
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

public sealed partial class DiscordLinkSystem : SharedDiscordBoostSystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private EuiManager _euis = default!;
    [Dependency] private IConfigurationManager _config = default!;
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IStatusHost _status = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private JobWhitelistManager _whitelist = default!;
    [Dependency] private IConnectionManager _connections = default!;
    [Dependency] private Content.Server.Players.PlayTimeTracking.PlayTimeTrackingManager _playTime = default!;
    private readonly HashSet<ICommonSession> _admitted = new();
    private readonly Dictionary<ICommonSession, long> _admissionRevisions = new();
    private DiscordLinkStore? _store;
    private string _token = "";
    private DateTime _nextBoostUpdate;
    private readonly Dictionary<ICommonSession, string> _boostPublished = new();

    public override bool HasActiveBoost(ICommonSession? session)
        => session != null && session.Channel.AuthType == LoginType.LoggedIn &&
           _enabled && (_store?.BoostRemaining(session.UserId.UserId) ?? 0) > 0;

    public override int GetBoostyTier(ICommonSession? session)
        => session != null && session.Channel.AuthType == LoginType.LoggedIn && _enabled
            ? Math.Max(_store?.BoostyStatus(session.UserId.UserId).Tier ?? 0, Content.Shared.Database.LagoonEconomyRules.GameTier(EntityManager.System<Content.Server._DeepLagoon.Currency.LagoonEconomySystem>().CachedExternalTier(session))) : 0;

    public override bool HasSpark(ICommonSession? session) => session != null && session.Channel.AuthType == LoginType.LoggedIn && _enabled && ((_store?.HasSpark(session.UserId.UserId) ?? false) || EntityManager.System<Content.Server._DeepLagoon.Currency.LagoonEconomySystem>().CachedExternalTier(session) >= 1);
    public int RawProductTier(ICommonSession session) => session.Channel.AuthType == LoginType.LoggedIn && _enabled
        ? Content.Shared.Database.LagoonEconomyRules.ProductTier(_store?.BoostyStatus(session.UserId.UserId).Tier ?? 0, _store?.HasSpark(session.UserId.UserId) ?? false, HasActiveBoost(session)) : 0;
    public override int GetPurchasedProductTier(ICommonSession? session) => EntityManager.System<Content.Server._DeepLagoon.Currency.LagoonEconomySystem>().PurchasedTier(session);
    public override int[] RoleColors => _store?.SupporterColors() ?? DefaultRoleColors;

    private void PublishBoosts()
    {
        var palette = _store?.SupporterColors();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var session in _players.Sessions)
        {
            if (session.Status != SessionStatus.InGame)
                continue;
            var remaining = HasActiveBoost(session) ? _store!.BoostRemaining(session.UserId.UserId) : 0;
            var tier = GetBoostyTier(session);
            var spark = HasSpark(session);
            var purchased = EntityManager.System<Content.Server._DeepLagoon.Currency.LagoonEconomySystem>().PurchasedStatus(session);
            var boostyRemaining = tier > 0 ? Math.Max(_store?.BoostyStatus(session.UserId.UserId).Remaining ?? 0,
                EntityManager.System<Content.Server._DeepLagoon.Currency.LagoonEconomySystem>().CachedExternalTier(session) > 1 ? 60 : 0) : 0;
            if (remaining == 0 && tier == 0 && !spark && purchased.Tier == 0 && palette == null && !_boostPublished.ContainsKey(session))
                continue;
            var colors = palette ?? DefaultRoleColors;
            var fingerprint = $"{now / 300}/{spark}/{purchased.Tier}/{purchased.Seconds}/{(remaining > 0 ? now + remaining : 0)}/{tier}/{(boostyRemaining > 0 ? now + boostyRemaining : 0)}/{string.Join(',', colors)}";
            if (_boostPublished.TryGetValue(session, out var previous) && previous == fingerprint)
                continue;
            _boostPublished[session] = fingerprint;
            RaiseNetworkEvent(new DiscordBoostStatusEvent(remaining, tier, boostyRemaining, colors, spark, purchased.Tier, purchased.Seconds), session);
        }
    }

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
        _whitelist.GlobalWhitelistRevoked += OnWhitelistRevoked;
        _status.AddHandler(HandleApi);
    }

    public static bool AdmissionAllowed(bool enabled, bool authenticated, bool linked, bool approved)
        => !enabled || (authenticated && linked && approved);

    private void OnWhitelistRevoked(NetUserId user)
    {
        if (AdmissionRequired && _players.TryGetSessionById(user, out var session))
            RevokeAdmission(session, "Допуск на сервер отозван. Обратитесь к регистраторам в Discord.", forceDisconnect: true);
    }

    public void RevokeAdmission(ICommonSession session, string reason, bool forceDisconnect = false)
    {
        _admissionRevisions[session] = _admissionRevisions.GetValueOrDefault(session) + 1;
        var hadAdmission = _admitted.Remove(session);
        if ((hadAdmission || forceDisconnect) && session.Channel.IsConnected)
            session.Channel.Disconnect(reason);
    }

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
            RevokeAdmission(session, "Привязка или авторизация аккаунта изменились. Войдите снова.");
            return;
        }
        try
        {
            var revision = _admissionRevisions.GetValueOrDefault(session);
            var approved = await _connections.CheckDiscordLobbyWhitelist(session.Channel.UserData);
            if (revision != _admissionRevisions.GetValueOrDefault(session) || !session.Channel.IsConnected) return;
            if (approved && session.Status == SessionStatus.InGame)
            {
                var newlyAdmitted = _admitted.Add(session);
                if (_prompts.Remove(session, out var prompt))
                    prompt.Close();
                if (newlyAdmitted)
                    EntityManager.System<Content.Server.GameTicking.GameTicker>().CompleteDiscordAdmission(session);
            }
            else
                RevokeAdmission(session, "Допуск на сервер отозван. Обратитесь к регистраторам в Discord.");
        }
        catch (Exception e)
        {
            RevokeAdmission(session, "Не удалось подтвердить допуск. Переподключитесь позже.");
            Log.Error($"Discord lobby admission failed: {e.GetType().Name}");
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (DateTime.UtcNow >= _nextBoostUpdate)
        {
            _nextBoostUpdate = DateTime.UtcNow.AddSeconds(5);
            PublishBoosts();
        }
        if (!_enabled || _checkingPrompts ||
            (_prompts.Count == 0 && !_config.GetCVar(CCVars.DiscordExternalAccounts)) || DateTime.UtcNow < _nextPromptCheck)
            return;
        _nextPromptCheck = DateTime.UtcNow.AddSeconds(3);
        RefreshPrompts();
    }

    private async void RefreshPrompts()
    {
        _checkingPrompts = true;
        try
        {
            var external = _config.GetCVar(CCVars.DiscordExternalAccounts);
            var sessions = external ? _players.Sessions.ToArray() : _prompts.Keys.ToArray();
            foreach (var session in sessions)
            {
                if (session.Status != SessionStatus.InGame || session.Channel.AuthType != LoginType.LoggedIn)
                    continue;
                if (external)
                    await _whitelist.RefreshGlobalWhitelistAsync(session);
                await RefreshAdmission(session);
            }
        }
        catch (Exception e) { Log.Error($"Discord WL refresh failed: {e.GetType().Name}"); }
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
        _store = new DiscordLinkStore(DiscordLinkStore.ResolvePath(root, _config.GetCVar(CCVars.DiscordLinkDatabaseFile)));
    }

    public override void Shutdown()
    {
        _enabled = false;
        _players.PlayerStatusChanged -= OnStatusChanged;
        _whitelist.GlobalWhitelistRevoked -= OnWhitelistRevoked;
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
            _boostPublished.Remove(args.Session);
            _prompts.Remove(args.Session);
            _admitted.Remove(args.Session);
            _admissionRevisions.Remove(args.Session);
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

    // The OAuth backend has an enrollment-only key; the bot retains administrative API access.
    private async Task<bool> HandleApi(IStatusHandlerContext context)
    {
        var path = context.Url.AbsolutePath;
        if (!path.StartsWith("/deeplagoon/discord/", StringComparison.Ordinal))
            return false;
        context.ResponseHeaders["Cache-Control"] = "no-store";
        if (context.RequestMethod != HttpMethod.Post ||
            path is not ("/deeplagoon/discord/lc_history" or "/deeplagoon/discord/lc_remove" or "/deeplagoon/discord/lc_add" or "/deeplagoon/discord/moderation" or "/deeplagoon/discord/supporter" or "/deeplagoon/discord/boost" or "/deeplagoon/discord/restore_discord" or "/deeplagoon/discord/reassign_discord" or "/deeplagoon/discord/enroll_launcher" or "/deeplagoon/discord/link" or "/deeplagoon/discord/lookup" or "/deeplagoon/discord/whitelist" or "/deeplagoon/discord/remove_whitelist" or "/deeplagoon/discord/unlink_discord"))
        {
            await context.RespondErrorAsync(HttpStatusCode.NotFound);
            return true;
        }
        var enrollment = path.EndsWith("/enroll_launcher", StringComparison.Ordinal);
        var scope = path.EndsWith("/supporter", StringComparison.Ordinal) || path.EndsWith("/boost", StringComparison.Ordinal)
            ? "supporter" : path.EndsWith("/moderation", StringComparison.Ordinal) ? "moderation"
            : path.Contains("/lc_", StringComparison.Ordinal) ? "coin" : "identity";
        var configured = enrollment ? "" : scope switch
        {
            "supporter" => _config.GetCVar(CCVars.DiscordSupporterToken),
            "moderation" => _config.GetCVar(CCVars.DiscordModerationToken),
            "coin" => _config.GetCVar(CCVars.DiscordCoinToken),
            _ => _config.GetCVar(CCVars.DiscordIdentityToken),
        };
        // Independent keys take precedence. Derived keys support coordinated
        // migration without transmitting the root key to any API endpoint.
        var expectedToken = configured.Length > 0 ? configured : _token.Length >= 32
            ? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_token), Encoding.UTF8.GetBytes(
                enrollment ? "lagoon-launcher-enrollment-v1" : "lagoon-api-" + scope + "-v1"))).ToLowerInvariant()
            : "";
        var authorized = _enabled && expectedToken.Length >= 32 &&
            IPAddress.IsLoopback(context.RemoteEndPoint.Address) &&
            context.RequestHeaders.TryGetValue("Authorization", out var auth) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth.ToString()), Encoding.UTF8.GetBytes("Bearer " + expectedToken));
        if (!authorized)
        {
            await context.RespondErrorAsync(HttpStatusCode.Unauthorized);
            return true;
        }
        if (_config.GetCVar(CCVars.DiscordExternalAccounts) &&
            (path.EndsWith("/whitelist", StringComparison.Ordinal) || path.EndsWith("/remove_whitelist", StringComparison.Ordinal) ||
             path.Contains("/lc_", StringComparison.Ordinal)))
        {
            await context.RespondJsonAsync(new { error = "account_service_required" }, HttpStatusCode.Gone);
            return true;
        }
        if (path.EndsWith("/lc_history", StringComparison.Ordinal) || path.EndsWith("/lc_add", StringComparison.Ordinal) || path.EndsWith("/lc_remove", StringComparison.Ordinal))
            return await HandleLagoonCoinApi(context);
        if (path.EndsWith("/moderation", StringComparison.Ordinal))
            return await HandleModerationApi(context);
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
        // A durable merge has its own per-account lock/job. Never hold the API gate
        // while waiting for an external worker or for players/database saves to drain.
        if (path.EndsWith("/link", StringComparison.Ordinal) &&
            request.Code?.Trim().ToUpperInvariant() is { Length: 24 } mergeCode && mergeCode.All(char.IsAsciiHexDigit))
        {
            var handled = await OnMainThread(() => HandleMergeRequest(request.DiscordId, mergeCode));
            if (handled.Status != HttpStatusCode.Continue)
            {
                await context.RespondJsonAsync(handled.Body, handled.Status);
                return true;
            }
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
                    if (path.EndsWith("/supporter", StringComparison.Ordinal))
                    {
                        if (request.BoostActive == null || request.BoostyTier is not (>= 0 and <= 3) ||
                            request.RoleColors is not { Length: 4 } || request.RoleColors.Any(c => c is < 0 or > 0xFFFFFF))
                            return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_request" });
                        _store.SetSupporter(request.DiscordId, request.BoostActive.Value, request.BoostyTier.Value, request.RoleColors, request.Spark);
                        PublishBoosts();
                        return new ApiResult(HttpStatusCode.OK, new { boost_active = request.BoostActive.Value, boosty_tier = request.BoostyTier.Value });
                    }
                    if (path.EndsWith("/boost", StringComparison.Ordinal))
                    {
                        if (request.BoostActive == null)
                            return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_request" });
                        _store.SetBoost(request.DiscordId, request.BoostActive.Value);
                        PublishBoosts();
                        return new ApiResult(HttpStatusCode.OK, new { boost_active = request.BoostActive.Value });
                    }
                    DiscordLinkStore.Link? link;
                    var merged = false;
                    if (path.EndsWith("/enroll_launcher", StringComparison.Ordinal))
                        link = _store.EnrollLauncher(request.DiscordId, request.DiscordUsername);
                    else if (path.EndsWith("/restore_discord", StringComparison.Ordinal))
                    {
                        if (!request.HostAuthorized || !Guid.TryParse(request.ExpectedUid, out var restoreUid) ||
                            string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 64)
                            return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_request" });
                        link = _store.RestoreDiscord(request.DiscordId, restoreUid, request.Username);
                    }
                    else if (path.EndsWith("/reassign_discord", StringComparison.Ordinal))
                    {
                        var target = request.TargetDiscordId;
                        if (!request.HostAuthorized || target == null || target.Length is < 15 or > 20 ||
                            target.Any(c => !char.IsAsciiDigit(c)) || !ulong.TryParse(target, out var targetId) || targetId == 0 ||
                            !Guid.TryParse(request.ExpectedUid, out var expectedUid) || request.ExpectedLinkedAt == null)
                            return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_request" });
                        link = _store.ReassignDiscord(request.DiscordId, target, expectedUid, request.ExpectedLinkedAt.Value, request.ExpectedRevision);
                        if (request.DiscordId != target)
                            foreach (var session in _players.Sessions.Where(s => s.UserId.UserId == link.Uid).ToArray())
                                session.Channel.Disconnect("Discord link changed by administrator. Sign in again.");
                    }
                    else if (path.EndsWith("/link", StringComparison.Ordinal))
                    {
                        var code = request.Code?.Trim().ToUpperInvariant() ?? "";
                        if (code.Length != 24 || code.Any(c => !char.IsAsciiHexDigit(c)))
                            return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_code" });
                        // Merge requests are handled above without the shared API semaphore.
                        if (_store.IsMergeCode(code)) return new ApiResult(HttpStatusCode.Conflict, new { error = "merge_pending" });
                        link = _store.Consume(request.DiscordId, code);
                    }
                    else
                        link = _store.FindDiscord(request.DiscordId);
                    if (link == null)
                        return new ApiResult(HttpStatusCode.NotFound, new { error = "not_linked" });
                    if (_store.MergePending(link.Uid) && !path.EndsWith("/lookup", StringComparison.Ordinal))
                        return new ApiResult(HttpStatusCode.Conflict, new { error = "merge_pending" });
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
                    if (path.EndsWith("/unlink_discord", StringComparison.Ordinal))
                    {
                        if (request.ExpectedUid != null || request.ExpectedLinkedAt != null)
                        {
                            if (!Guid.TryParse(request.ExpectedUid, out var expectedUid) || request.ExpectedLinkedAt == null)
                                return new ApiResult(HttpStatusCode.BadRequest, new { error = "invalid_request" });
                            _store.AssertCurrent(request.DiscordId, expectedUid, request.ExpectedLinkedAt.Value, request.ExpectedRevision);
                        }
                        if (!request.HostAuthorized && await _database.GetAdminDataForAsync(new NetUserId(link.Uid)) != null)
                            return new ApiResult(HttpStatusCode.Forbidden, new { error = "admin_protected" });
                        _store.Unlink(request.DiscordId, link.Uid);
                        existing = true;
                    }
                    PublishBoosts();
                    await RefreshUid(link.Uid);
                    return new ApiResult(HttpStatusCode.OK, new { uid = link.Uid, username = link.Username, existing, merged });
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

    private async Task<DiscordLinkStore.Link> MergeAccounts(string discordId, string code)
    {
        if (_store == null) throw new DiscordLinkStore.LinkException("unavailable");
        var worker = _config.GetCVar(CCVars.DiscordAccountMergeWorker);
        var python = _config.GetCVar(CCVars.DiscordAccountMergePython);
        if (!File.Exists(worker) || !File.Exists(python))
            throw new DiscordLinkStore.LinkException("merge_unavailable");
        var check = new System.Diagnostics.ProcessStartInfo(python)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        check.ArgumentList.Add(worker);
        check.ArgumentList.Add("--check");
        using (var preflight = System.Diagnostics.Process.Start(check))
        {
            if (preflight == null) throw new DiscordLinkStore.LinkException("merge_unavailable");
            var checkOut = preflight.StandardOutput.ReadToEndAsync();
            var checkError = preflight.StandardError.ReadToEndAsync();
            try { await preflight.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                // --check is read-only; unlike the real worker it is safe to terminate.
                if (!preflight.HasExited) preflight.Kill(entireProcessTree: true);
                await preflight.WaitForExitAsync();
                await checkOut;
                await checkError;
                throw new DiscordLinkStore.LinkException("merge_unavailable");
            }
            await checkOut;
            await checkError;
            if (preflight.ExitCode != 0) throw new DiscordLinkStore.LinkException("merge_unavailable");
        }
        // Drain earlier administrative mutations before installing the durable account barrier.
        DiscordLinkStore.MergePlan plan;
        await _apiLock.WaitAsync();
        try { plan = _store.PrepareMerge(discordId, code); }
        finally { _apiLock.Release(); }
        if (_store.MergeCompleted(discordId, plan))
            return new DiscordLinkStore.Link(plan.CanonicalUid, plan.Username);
        foreach (var session in _players.Sessions.Where(s => s.UserId.UserId == plan.OfficialUid || s.UserId.UserId == plan.CanonicalUid).ToArray())
            session.Channel.Disconnect("Аккаунты объединяются. Переподключитесь после подтверждения в Discord.");
        // The identity gateway blocks both subjects while the durable plan is
        // pending. Wait for disconnect callbacks and their database writes.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_players.Sessions.Any(s => s.UserId.UserId == plan.OfficialUid || s.UserId.UserId == plan.CanonicalUid) || ServerDbManager.DbActiveOps.Value > 0)
        {
            if (DateTime.UtcNow > deadline) throw new DiscordLinkStore.LinkException("merge_pending");
            await Task.Delay(100);
        }
        await _playTime.WaitPendingSavesAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var start = new System.Diagnostics.ProcessStartInfo(python)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(worker);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new DiscordLinkStore.LinkException("merge_unavailable");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { source_uid = plan.OfficialUid, target_uid = plan.CanonicalUid }));
        process.StandardInput.Close();
        // Do not kill a worker after an HTTP timeout: it may already have
        // committed. The PostgreSQL journal makes a subsequent retry safe.
        await process.WaitForExitAsync();
        var output = await outputTask;
        await errorTask; // drain, never log credentials or database rows
        if (process.ExitCode != 0) throw new DiscordLinkStore.LinkException("merge_pending");
        using var reply = JsonDocument.Parse(output);
        if (!reply.RootElement.GetProperty("ok").GetBoolean() ||
            reply.RootElement.GetProperty("source_uid").GetString() != plan.OfficialUid.ToString() ||
            reply.RootElement.GetProperty("target_uid").GetString() != plan.CanonicalUid.ToString())
            throw new DiscordLinkStore.LinkException("merge_pending");
        var link = _store.CompleteMerge(discordId, code, plan);
        var library = EntityManager.System<Content.Server._DeepLagoon.InteractionPanel.InteractionPanelSystem>();
        library.InvalidateAccountLibrary(new NetUserId(plan.OfficialUid));
        library.InvalidateAccountLibrary(new NetUserId(plan.CanonicalUid));
        return link;
    }

    private sealed record ApiRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("discord_id")] string DiscordId,
        [property: System.Text.Json.Serialization.JsonPropertyName("code")] string? Code,
        [property: System.Text.Json.Serialization.JsonPropertyName("host_authorized")] bool HostAuthorized = false,
        [property: System.Text.Json.Serialization.JsonPropertyName("target_discord_id")] string? TargetDiscordId = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("expected_uid")] string? ExpectedUid = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("expected_linked_at")] long? ExpectedLinkedAt = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("username")] string? Username = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("expected_revision")] long? ExpectedRevision = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("discord_username")] string? DiscordUsername = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("boost_active")] bool? BoostActive = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("boosty_tier")] int? BoostyTier = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("role_colors")] int[]? RoleColors = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("spark")] bool Spark = false);
    private sealed record ApiResult(HttpStatusCode Status, object Body);
}
