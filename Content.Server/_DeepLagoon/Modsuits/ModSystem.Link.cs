using System.Globalization;
using System.Linq;
using System.Numerics;
using Content.Server.Chat.Systems;
using Content.Shared._DeepLagoon.Modsuits;
using Content.Shared.Actions;
using Content.Shared.Alert;
using Content.Shared.Chat;
using Content.Shared.Clothing;
using Content.Shared.Examine;
using Content.Shared.IdentityManagement;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._DeepLagoon.Modsuits;

public sealed partial class ModSystem
{
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private AlertsSystem _alerts = default!;

    private readonly HashSet<string> _numbers = new();
    private readonly Dictionary<EntityUid, EntityUid> _callsByWearer = new();

    private void InitializeLink()
    {
        SubscribeLocalEvent<ModLinkComponent, MapInitEvent>(OnLinkInit);
        SubscribeLocalEvent<ModLinkComponent, ComponentShutdown>(OnLinkShutdown);
        SubscribeLocalEvent<ModLinkComponent, ClothingGotEquippedEvent>(OnEquipLink);
        SubscribeLocalEvent<ModLinkComponent, ClothingGotUnequippedEvent>(OnUnequipLink);
        SubscribeLocalEvent<ModLinkComponent, GetItemActionsEvent>(OnActions);
        SubscribeLocalEvent<ModLinkComponent, OpenModControlEvent>(OnAction);
        SubscribeLocalEvent<ModLinkComponent, GetVerbsEvent<AlternativeVerb>>(OnLinkVerbs);
        SubscribeLocalEvent<ModLinkComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<ModLinkComponent, ModUiMessage>(OnUiMessage);
        SubscribeLocalEvent<ModLinkComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<AlertsComponent, ModLinkIncomingCallEvent>(OnIncomingCallAlert);
        SubscribeLocalEvent<EntitySpokeEvent>(OnSpeak);
        InitializeProjection();
    }

    private void OnLinkInit(Entity<ModLinkComponent> device, ref MapInitEvent args)
    {
        string number;
        do
        {
            number = _random.Next(100000, 1000000).ToString(CultureInfo.InvariantCulture);
        } while (!_numbers.Add(number));

        device.Comp.Number = number;
    }

    private void OnLinkShutdown(Entity<ModLinkComponent> device, ref ComponentShutdown args)
    {
        EndCall(device);
        _numbers.Remove(device.Comp.Number);
        _actions.RemoveAction(device.Comp.Action);
    }

    private void OnEquipLink(Entity<ModLinkComponent> device, ref ClothingGotEquippedEvent args)
    {
        device.Comp.Wearer = args.Wearer;
        CloseModuleStorage(device);
        _ui.CloseUi(device.Owner, ModUiKey.Key);
    }

    private void OnUnequipLink(Entity<ModLinkComponent> device, ref ClothingGotUnequippedEvent args)
    {
        if (TryComp<ModControlComponent>(device, out var suit))
            StopModules((device.Owner, suit));

        EndCall(device);
        device.Comp.Wearer = null;
        CloseModuleStorage(device);
        _ui.CloseUi(device.Owner, ModUiKey.Key);
    }

    private void OnActions(Entity<ModLinkComponent> device, ref GetItemActionsEvent args)
    {
        var slot = device.Comp.Slot == "neck" ? SlotFlags.NECK : SlotFlags.BACK;
        if (args.SlotFlags == slot)
            args.AddAction(ref device.Comp.Action, "ActionModControl");
    }

    private void OnAction(Entity<ModLinkComponent> device, ref OpenModControlEvent args)
    {
        if (args.Handled || !IsWorn(device, args.Performer))
            return;

        args.Handled = true;
        if (_ui.IsUiOpen(device.Owner, ModUiKey.Key, args.Performer))
            _ui.CloseUi(device.Owner, ModUiKey.Key, args.Performer);
        else
            Open(device, args.Performer);
    }

    private void OnUiOpened(Entity<ModLinkComponent> device, ref BoundUIOpenedEvent args)
    {
        if (!Equals(args.UiKey, ModUiKey.Key))
            return;

        if (!CanUse(device, args.Actor))
            _ui.CloseUi(device.Owner, ModUiKey.Key, args.Actor);
        else
            RefreshUi(device);
    }

    private void OnExamine(Entity<ModLinkComponent> device, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("mod-link-examine", ("number", device.Comp.Number),
            ("frequency", device.Comp.Frequency)));
        if (HasComp<ModControlComponent>(device))
            args.PushMarkup(Loc.GetString("mod-examine"));
    }

    private void OnLinkVerbs(Entity<ModLinkComponent> device, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        if (!args.CanAccess || !args.CanInteract || !CanUse(device, user))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("mod-control-title"),
            Act = () => Open(device, user),
        });

        if (!TryGetMultitool(user, out var tool))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("mod-link-copy-frequency"),
            Act = () =>
            {
                if (CanUse(device, user) && _hands.IsHolding(user, tool))
                    EnsureComp<ModLinkMultitoolComponent>(tool).Frequency = device.Comp.Frequency;
            },
        });

        if (!TryComp<ModLinkMultitoolComponent>(tool, out var buffer))
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("mod-link-paste-frequency"),
            Act = () =>
            {
                if (CanUse(device, user) && _hands.IsHolding(user, tool))
                    SetFrequency(device, buffer.Frequency);
            },
        });
    }

    private bool TryGetMultitool(EntityUid user, out EntityUid tool)
    {
        tool = default;
        if (!_hands.TryGetActiveItem(user, out var held) || !_tools.HasQuality(held.Value, "Pulsing"))
            return false;

        tool = held.Value;
        return true;
    }

    private bool HasMultitool(EntityUid user) => TryGetMultitool(user, out _);

    private void SetFrequency(Entity<ModLinkComponent> device, int frequency)
    {
        if (device.Comp.Frequency == frequency)
            return;

        EndCall(device);
        device.Comp.Frequency = frequency;
        RefreshUi(device);
    }

    private bool CanCall(Entity<ModLinkComponent> device)
    {
        return device.Comp.Frequency > 0 && device.Comp.Wearer is {} user &&
               !_mobState.IsDead(user) && IsWorn(device, user) &&
               (!HasComp<ModControlComponent>(device) || HasCore(device)) &&
               _power.HasCharge(device, device.Comp.CallDraw * TickInterval);
    }

    private bool CanDial(Entity<ModLinkComponent> device, Entity<ModLinkComponent> peer)
    {
        return device.Owner != peer.Owner && device.Comp.Wearer != peer.Comp.Wearer &&
               device.Comp.Frequency == peer.Comp.Frequency &&
               device.Comp.Peer == null && peer.Comp.Peer == null &&
               CanCall(device) && CanCall(peer) &&
               !_callsByWearer.ContainsKey(device.Comp.Wearer!.Value) &&
               !_callsByWearer.ContainsKey(peer.Comp.Wearer!.Value);
    }

    private static string DisplayName(ModLinkComponent device)
    {
        return string.IsNullOrEmpty(device.Label) ? device.Number : $"{device.Label} ({device.Number})";
    }

    private ModContact[] Contacts(Entity<ModLinkComponent> device)
    {
        if (!CanCall(device))
            return [];

        var contacts = new List<ModContact>();
        var query = EntityQueryEnumerator<ModLinkComponent>();
        while (query.MoveNext(out var uid, out var peer))
        {
            if (CanDial(device, (uid, peer)))
                contacts.Add(new(GetNetEntity(uid), DisplayName(peer)));
        }

        return contacts.OrderBy(contact => contact.Name, StringComparer.Ordinal).ToArray();
    }

    private void HandleLinkMessage(Entity<ModLinkComponent> device, ModUiMessage args)
    {
        switch (args.Operation)
        {
            case ModOperation.Call:
                if (TryGetEntity(args.Target, out var uid) && TryComp<ModLinkComponent>(uid, out var peer) &&
                    CanDial(device, (uid.Value, peer)))
                    Dial(device, (uid.Value, peer));
                break;
            case ModOperation.Accept:
                Accept(device);
                break;
            case ModOperation.End:
                EndCall(device);
                break;
            case ModOperation.Rename:
                if (args.Text is { Length: <= 32 } label && !label.Any(char.IsControl))
                    device.Comp.Label = label.Trim();
                break;
            case ModOperation.SetFrequency:
                if (HasMultitool(args.Actor) && args.Text is { Length: 4 } input &&
                    int.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var frequency) &&
                    frequency is >= 1000 and <= 1999)
                    SetFrequency(device, frequency);
                break;
        }
    }

    private void Dial(Entity<ModLinkComponent> caller, Entity<ModLinkComponent> receiver)
    {
        var deadline = _timing.CurTime + TimeSpan.FromSeconds(10);
        caller.Comp.Peer = receiver;
        caller.Comp.RingUntil = deadline;
        receiver.Comp.Peer = caller;
        receiver.Comp.RingUntil = deadline;
        receiver.Comp.Incoming = true;

        var recipient = receiver.Comp.Wearer!.Value;
        _callsByWearer.Add(caller.Comp.Wearer!.Value, caller);
        _callsByWearer.Add(recipient, receiver);
        _audio.PlayEntity(new SoundPathSpecifier("/Audio/Machines/twobeep.ogg"), recipient, receiver.Owner);
        _alerts.ShowAlert(recipient, "ModLinkIncomingCall", cooldown: (_timing.CurTime, deadline), autoRemove: true);
        RefreshUi(receiver);
    }

    private void OnIncomingCallAlert(Entity<AlertsComponent> wearer, ref ModLinkIncomingCallEvent args)
    {
        if (args.Handled || !_callsByWearer.TryGetValue(wearer, out var device) ||
            !TryComp<ModLinkComponent>(device, out var link) || link.Wearer != wearer.Owner ||
            !link.Incoming || link.Connected || link.RingUntil <= _timing.CurTime || !CanCall((device, link)))
            return;

        args.Handled = true;
        Open((device, link), wearer);
    }

    private void Accept(Entity<ModLinkComponent> receiver)
    {
        if (!receiver.Comp.Incoming || receiver.Comp.Connected || receiver.Comp.RingUntil <= _timing.CurTime ||
            receiver.Comp.Peer is not {} peerUid || !TryComp<ModLinkComponent>(peerUid, out var peer) ||
            peer.Peer != receiver.Owner || !CanCall(receiver) || !CanCall((peerUid, peer)))
            return;

        receiver.Comp.Connected = true;
        peer.Connected = true;
        _modAppearance.SetData(receiver.Owner, ModLinkVisuals.Connected, true);
        _modAppearance.SetData(peerUid, ModLinkVisuals.Connected, true);
        _alerts.ClearAlert(receiver.Comp.Wearer!.Value, "ModLinkIncomingCall");
        receiver.Comp.Hologram = SpawnHologram(receiver.Comp.Wearer!.Value, peer.Wearer!.Value);
        peer.Hologram = SpawnHologram(peer.Wearer.Value, receiver.Comp.Wearer.Value);
        RefreshUi(peerUid);
    }

    private EntityUid SpawnHologram(EntityUid speaker, EntityUid listener)
    {
        var hologram = Spawn("ModLinkHologram", new EntityCoordinates(listener, new Vector2(0.52f, 0)));
        UpdateProjectionName(hologram, speaker);
        SpawnProjectionAvatar(hologram, speaker);
        return hologram;
    }

    private void UpdateProjectionName(EntityUid hologram, EntityUid speaker)
    {
        var name = Loc.GetString("mod-link-hologram-name", ("name", Identity.Name(speaker, EntityManager)));
        EntityManager.System<MetaDataSystem>().SetEntityName(hologram, name);
    }

    private void UpdateLink(Entity<ModLinkComponent> device)
    {
        if (!CanCall(device) || device.Comp.Peer is not { } peerUid ||
            !TryComp<ModLinkComponent>(peerUid, out var peer) || peer.Peer != device.Owner ||
            peer.Frequency != device.Comp.Frequency || !CanCall((peerUid, peer)) ||
            (!device.Comp.Connected && device.Comp.RingUntil <= _timing.CurTime) ||
            (device.Comp.Connected && !_power.TryUseCharge(device, device.Comp.CallDraw * TickInterval)))
        {
            EndCall(device);
            return;
        }

        if (device.Comp.Connected && device.Comp.Wearer is { } speaker)
            RefreshProjectionAvatar(speaker);
    }

    private void EndCall(Entity<ModLinkComponent> device)
    {
        var peerUid = device.Comp.Peer;
        ClearCall(device);
        if (peerUid is {} uid && TryComp<ModLinkComponent>(uid, out var peer) && peer.Peer == device.Owner)
        {
            ClearCall((uid, peer));
            RefreshUi(uid);
        }

        RefreshUi(device);
    }

    private void ClearCall(Entity<ModLinkComponent> device)
    {
        var link = device.Comp;
        if (link.Peer != null && link.Wearer is {} user)
        {
            _callsByWearer.Remove(user);
            _projectionAvatars.Remove(user);
            if (link.Incoming)
                _alerts.ClearAlert(user, "ModLinkIncomingCall");
        }

        QueueDel(link.Hologram);
        link.Hologram = null;
        link.Peer = null;
        if (link.Connected && !TerminatingOrDeleted(device))
            _modAppearance.SetData(device.Owner, ModLinkVisuals.Connected, false);
        link.Connected = false;
        link.Incoming = false;
    }

    private void OnSpeak(EntitySpokeEvent args)
    {
        if (args.WasRadioSpeech || !_callsByWearer.TryGetValue(args.Source, out var uid) ||
            !TryComp<ModLinkComponent>(uid, out var link) || !link.Connected || link.Hologram is not {} hologram ||
            !CanCall((uid, link)) || link.Peer is not {} peerUid ||
            !TryComp<ModLinkComponent>(peerUid, out var peer) || !CanCall((peerUid, peer)))
            return;

        UpdateProjectionName(hologram, args.Source);
        _chat.TrySendInGameICMessage(hologram, args.Message,
            args.IsWhisper ? InGameICChatType.Whisper : InGameICChatType.Speak, ChatTransmitRange.GhostRangeLimit,
            hideLog: true, nameOverride: Name(hologram), checkRadioPrefix: false, ignoreActionBlocker: true, languageOverride: args.Language);
    }
}
