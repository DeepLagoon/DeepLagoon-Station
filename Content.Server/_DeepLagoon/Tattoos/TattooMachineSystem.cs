using System.Linq;
using Content.Shared._DeepLagoon.Tattoos;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Configuration;
using Robust.Shared.Player;
using Robust.Shared.Enums;
using Content.Shared.Mind;

namespace Content.Server._DeepLagoon.Tattoos;

/// <summary>Round-local tattooing. Profile tattoos are saved separately by the character editor.</summary>
public sealed partial class TattooMachineSystem : EntitySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private INetConfigurationManager _cfg = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    private float _preferenceTimer;
    private readonly Dictionary<EntityUid, int> _revisions = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<TattooMachineComponent, AfterInteractEvent>(OnInteract);
        SubscribeLocalEvent<TattooMachineComponent, TattooMachineApplyMessage>(OnApply);
        SubscribeLocalEvent<TattooMachineComponent, ComponentShutdown>((uid, _, _) => _revisions.Remove(uid));
        SubscribeLocalEvent<HumanoidAppearanceComponent, PlayerAttachedEvent>((uid, comp, ev) => SyncPreference(uid, comp));
    }

    public bool TattoosAllowed(EntityUid uid)
    {
        ICommonSession? session = null;
        if (_players.TryGetSessionByEntity(uid, out var attached)) session = attached;
        else if (_mind.TryGetMind(uid, out _, out var mind) && mind.UserId is { } user
            && _players.TryGetSessionById(user, out var owner)) session = owner;
        if (session != null && session.Status != SessionStatus.Disconnected)
            return _cfg.GetClientCVar(session.Channel, TattooCVars.Enabled);
        // Preserve the last choice while disconnected; NPCs default to enabled.
        return !TryComp<HumanoidAppearanceComponent>(uid, out var humanoid) || humanoid.TattoosEnabled;
    }

    private void SyncPreference(EntityUid uid, HumanoidAppearanceComponent humanoid)
    {
        var enabled = TattoosAllowed(uid);
        if (humanoid.TattoosEnabled == enabled) return;
        humanoid.TattoosEnabled = enabled;
        Dirty(uid, humanoid);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _preferenceTimer += frameTime;
        if (_preferenceTimer < .25f) return;
        _preferenceTimer = 0;
        var bodies = EntityQueryEnumerator<HumanoidAppearanceComponent>();
        while (bodies.MoveNext(out var uid, out var humanoid)) SyncPreference(uid, humanoid);
        var machines = EntityQueryEnumerator<TattooMachineComponent>();
        while (machines.MoveNext(out var uid, out var machine))
            if (machine.Artist is { } artist && machine.Target is { } target
                && (!TattoosAllowed(artist) || !TattoosAllowed(target))) _ui.CloseUis(uid);
    }

    private bool CanUse(EntityUid machine, EntityUid artist, EntityUid target) => Exists(target)
        && _hands.IsHolding(artist, machine, out _) && _interaction.InRangeUnobstructed(artist, target)
        && HasComp<HumanoidAppearanceComponent>(target) && TattoosAllowed(artist) && TattoosAllowed(target);

    public bool CanEditStoredInk(ICommonSession session, Tattoo basis)
    {
        if (session.AttachedEntity is not { } artist) return false;
        var machines = EntityQueryEnumerator<TattooMachineComponent>();
        while (machines.MoveNext(out var uid, out var machine))
            if (machine.Artist == artist && machine.Target is { } target && CanUse(uid, artist, target)
                && Comp<HumanoidAppearanceComponent>(target).Tattoos.Any(t => t.Id == basis.Id
                    && t.NormalBitmap == basis.NormalBitmap && t.GlowBitmap == basis.GlowBitmap
                    && t.Strokes.Count == basis.Strokes.Count && t.Strokes.Zip(basis.Strokes).All(p => p.First.ContentEquals(p.Second)))) return true;
        return false;
    }

    private void OnInteract(EntityUid uid, TattooMachineComponent component, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target || !CanUse(uid, args.User, target))
            return;
        _ui.CloseUis(uid);
        component.Target = target;
        component.Artist = args.User;
        _revisions[uid] = _revisions.GetValueOrDefault(uid) + 1;
        Publish(uid, component);
        _ui.TryOpenUi(uid, TattooMachineUiKey.Key, args.User);
        args.Handled = true;
    }

    private void OnApply(EntityUid uid, TattooMachineComponent component, TattooMachineApplyMessage args)
    {
        if (component.Target is not { } target || component.Artist != args.Actor || !CanUse(uid, args.Actor, target))
        {
            _ui.CloseUis(uid);
            return;
        }
        var humanoid = Comp<HumanoidAppearanceComponent>(target);
        if (args.UiKey is not TattooMachineUiKey.Key || args.Revision != _revisions.GetValueOrDefault(uid)
            || args.Tattoos == null || args.Tattoos.Count > Tattoo.MaxTattoos
            || args.Tattoos.Any(t => t == null || !t.IsValid()) || args.Tattoos.Select(t => t.Id).Distinct().Count() != args.Tattoos.Count)
        {
            Publish(uid, component, "Рисунок изменился или превышен лимит. Повторите действие.");
            return;
        }
        if (!_players.TryGetSessionByEntity(args.Actor, out var artistSession)
            || args.Tattoos.Any(t => !EntityManager.System<TattooArtworkSystem>().Accept(artistSession.UserId, t, humanoid.Tattoos)))
        {
            Publish(uid, component, "Сервер не подтвердил происхождение рисунка. Завершите рисование или импорт повторно.");
            return;
        }
        // Check every changed AND removed tattoo. Covered/missing skin cannot be tattooed or erased.
        var changed = humanoid.Tattoos.Where(old => !args.Tattoos.Any(next => old.ContentEquals(next)))
            .Concat(args.Tattoos.Where(next => !humanoid.Tattoos.Any(old => old.ContentEquals(next))));
        if (changed.Any(t => humanoid.HiddenLayers.ContainsKey(t.BodyPart) || humanoid.PermanentlyHidden.Contains(t.BodyPart)
            || !TattooBody.Allows(humanoid, t.BodyPart, _prototypes)))
        {
            Publish(uid, component, "Эта часть тела закрыта одеждой, отсутствует или не допускает татуировки.");
            return;
        }
        humanoid.Tattoos = Tattoo.Sanitize(args.Tattoos);
        Dirty(target, humanoid);
        _revisions[uid]++;
        Publish(uid, component);
    }

    private void Publish(EntityUid uid, TattooMachineComponent component, string status = "")
    {
        if (component.Target is not { } target || !TryComp<HumanoidAppearanceComponent>(target, out var humanoid)) return;
        _ui.SetUiState(uid, TattooMachineUiKey.Key,
            new TattooMachineState(GetNetEntity(target), Tattoo.Sanitize(humanoid.Tattoos), status) { Revision = _revisions.GetValueOrDefault(uid) });
    }
}
