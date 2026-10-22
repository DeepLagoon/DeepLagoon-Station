using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Content.Server.Preferences.Managers;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared._DeepLagoon.Tattoos;
using Content.Shared.Preferences;
using Robust.Shared.ContentPack;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._DeepLagoon.Tattoos;

public sealed partial class TattooArtworkSystem : EntitySystem
{
    [Dependency] private IResourceManager _resources = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IGameTiming _timing = default!;
    private TattooArtworkProof _proof = default!;
    private readonly Dictionary<NetUserId, TimeSpan> _cooldowns = new();
    private readonly HashSet<NetUserId> _busy = new();
    public override void Initialize()
    {
        var path = new ResPath("/tattoo-authority.key");
        byte[] key;
        if (_resources.UserData.Exists(path)) key = _resources.UserData.ReadAllBytes(path);
        else { key = RandomNumberGenerator.GetBytes(32); using var file = _resources.UserData.Open(path, FileMode.CreateNew); file.Write(key); }
        if (!OperatingSystem.IsWindows() && _resources.UserData.RootDir is { } root)
            File.SetUnixFileMode(Path.Combine(root, "tattoo-authority.key"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (key.Length != 32) throw new InvalidDataException("Invalid tattoo authority key.");
        _proof = new TattooArtworkProof(key);
        SubscribeNetworkEvent<TattooArtworkRequest>(OnRequest);
    }
    public bool Accept(NetUserId user, Tattoo artwork, IEnumerable<Tattoo> previous)
    {
        if (!artwork.IsValid()) return false;
        if (artwork.NormalBitmap.Length == 0 && artwork.GlowBitmap.Length == 0) return true; // Validated vector strokes remain free.
        if (_proof.Verify(user.UserId, artwork)) return true;
        // Grandfather only unchanged stored ink, never a client assertion that a bitmap was drawn here.
        return previous.Any(old => SameInk(old, artwork));
    }
    private static bool SameInk(Tattoo a, Tattoo b) => a.NormalBitmap == b.NormalBitmap && a.GlowBitmap == b.GlowBitmap
        && a.Strokes.Count == b.Strokes.Count && a.Strokes.Zip(b.Strokes).All(p => p.First.ContentEquals(p.Second));
    private bool OwnStored(NetUserId user, Tattoo basis) => _preferences.TryGetCachedPreferences(user, out var prefs)
        && prefs.Characters.Values.OfType<HumanoidCharacterProfile>().Any(p => p.Appearance.Tattoos.Any(t => SameInk(t, basis)));
    private async void OnRequest(TattooArtworkRequest ev, EntitySessionEventArgs args)
    {
        var session = args.SenderSession;
        void Reply(Tattoo? result, string error = "") { if (session.Channel.IsConnected) RaiseNetworkEvent(new TattooArtworkReply(ev.Request, result, error), session.Channel); }
        if (_busy.Count >= 2 || _busy.Contains(session.UserId) || _cooldowns.GetValueOrDefault(session.UserId) > _timing.CurTime)
        { Reply(null, "Сервер обрабатывает рисунки. Повторите через секунду."); return; }
        if (_cooldowns.Count > 4096) foreach (var id in _cooldowns.Where(p => p.Value <= _timing.CurTime).Select(p => p.Key).ToArray()) _cooldowns.Remove(id);
        _cooldowns[session.UserId] = _timing.CurTime + TimeSpan.FromSeconds(1);
        if (ev.Basis == null || ev.Recipe == null || !ev.Basis.IsValid() || ev.Recipe.Length > 240000)
        { Reply(null, "Некорректный рисунок."); return; }
        var imported = !_proof.Verify(session.UserId.UserId, ev.Basis) && !OwnStored(session.UserId, ev.Basis)
            && !EntityManager.System<TattooMachineSystem>().CanEditStoredInk(session, ev.Basis)
            && (ev.Basis.NormalBitmap.Length > 0 || ev.Basis.GlowBitmap.Length > 0);
        if (imported && EntityManager.System<SharedDiscordBoostSystem>().GetSupporterTier(session) < 3)
        { Reply(null, "Импорт стороннего PNG требует третий тир доната. Собственные подписанные экспорты доступны бесплатно."); return; }
        _busy.Add(session.UserId);
        try
        {
            var basis = ev.Basis.Clone();
            var recipe = ev.Recipe.Length == 0 ? "[{\"useBase\":true}]" : ev.Recipe;
            var result = await Task.Run(() => TattooArtworkRenderer.Render(basis, recipe));
            // Entitlement may have expired while the worker was running.
            if (imported && EntityManager.System<SharedDiscordBoostSystem>().GetSupporterTier(session) < 3)
            { Reply(null, "Право импорта истекло. Повторите после обновления статуса."); return; }
            result.ArtworkProof = _proof.Sign(session.UserId.UserId, result); Reply(result);
        }
        catch (Exception) { Reply(null, "Рисунок повреждён или слишком сложный. Уменьшите число штрихов и повторите."); }
        finally { _busy.Remove(session.UserId); }
    }
}
