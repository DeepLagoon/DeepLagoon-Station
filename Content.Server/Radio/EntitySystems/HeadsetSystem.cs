using Content.Server.Chat.Systems;
using Content.Server.Emp;
using Content.Server._Mono.Radio;
using Content.Shared._Mono.Radio;
using Content.Shared.Inventory.Events;
using Content.Shared.Radio;
using Content.Shared.Radio.Components;
using Content.Server.Speech;
using Content.Server._EinsteinEngines.Language;

using Content.Shared.Chat;
using Content.Shared.Radio.EntitySystems;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Configuration;

namespace Content.Server.Radio.EntitySystems;

public sealed partial class HeadsetSystem : SharedHeadsetSystem
{
    [Dependency] private INetManager _netMan = default!;
    [Dependency] private RadioSystem _radio = default!;
    [Dependency] private LanguageSystem _language = default!;
    [Dependency] private HeadsetPunishmentSystem _punishment = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HeadsetComponent, RadioReceiveEvent>(OnHeadsetReceive);
        SubscribeLocalEvent<HeadsetComponent, EncryptionChannelsChangedEvent>(OnKeysChanged);

        SubscribeLocalEvent<WearingHeadsetComponent, EntitySpokeEvent>(OnSpeak);
    }

    private void OnKeysChanged(EntityUid uid, HeadsetComponent component, EncryptionChannelsChangedEvent args)
    {
        UpdateRadioChannels(uid, component, args.Component);
    }

    private void UpdateRadioChannels(EntityUid uid, HeadsetComponent headset, EncryptionKeyHolderComponent? keyHolder = null)
    {
        // make sure to not add ActiveRadioComponent when headset is being deleted
        if (!headset.Enabled || MetaData(uid).EntityLifeStage >= EntityLifeStage.Terminating)
            return;

        if (!Resolve(uid, ref keyHolder))
            return;

        if (keyHolder.Channels.Count == 0)
            RemComp<ActiveRadioComponent>(uid);
        else
        {
            var channels = new HashSet<ProtoId<RadioChannelPrototype>>();
            foreach (var channel in keyHolder.Channels)
                channels.Add(channel);
            EnsureComp<ActiveRadioComponent>(uid).Channels = channels;
        }
    }

    private void OnSpeak(EntityUid uid, WearingHeadsetComponent component, EntitySpokeEvent args)
    {
        var channel = args.Channel;
        if (channel == null)
            return;

        foreach (var headset in component.Headsets)
        {
            if (!TryComp<HeadsetComponent>(headset, out var radio) || !radio.Enabled || !radio.IsEquipped
                || !TryComp<EncryptionKeyHolderComponent>(headset, out var keys) || !keys.Channels.Contains(channel.ID))
                continue;

            _punishment.Punish(uid, headset, args.Message);
            _radio.SendRadioMessage(uid, args.Message, channel, headset);
            args.Channel = null;
            return;
        }
    }

    private void AddWearingHeadset(EntityUid wearer, EntityUid headset)
    {
        EnsureComp<WearingHeadsetComponent>(wearer).Headsets.Add(headset);
    }

    private void RemoveWearingHeadset(EntityUid wearer, EntityUid headset)
    {
        if (!TryComp<WearingHeadsetComponent>(wearer, out var wearing))
            return;
        wearing.Headsets.Remove(headset);
        if (wearing.Headsets.Count == 0)
            RemCompDeferred<WearingHeadsetComponent>(wearer);
    }

    protected override void OnGotEquipped(EntityUid uid, HeadsetComponent component, GotEquippedEvent args)
    {
        base.OnGotEquipped(uid, component, args);
        if (component.IsEquipped && component.Enabled)
        {
            AddWearingHeadset(args.Equipee, uid);
            UpdateRadioChannels(uid, component);
        }
    }

    protected override void OnGotUnequipped(EntityUid uid, HeadsetComponent component, GotUnequippedEvent args)
    {
        base.OnGotUnequipped(uid, component, args);
        RemComp<ActiveRadioComponent>(uid);
        RemoveWearingHeadset(args.Equipee, uid);
    }

    public void SetEnabled(EntityUid uid, bool value, HeadsetComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.Enabled == value)
            return;

        component.Enabled = value;
        Dirty(uid, component);

        if (!value)
        {
            RemCompDeferred<ActiveRadioComponent>(uid);

            if (component.IsEquipped)
                RemoveWearingHeadset(Transform(uid).ParentUid, uid);
        }
        else if (component.IsEquipped)
        {
            AddWearingHeadset(Transform(uid).ParentUid, uid);
            UpdateRadioChannels(uid, component);
        }
    }

    private void OnHeadsetReceive(EntityUid uid, HeadsetComponent component, ref RadioReceiveEvent args)
    {
        if (TryComp(Transform(uid).ParentUid, out ActorComponent? actor))
        {
            // Einstein Engines - Language begin
            var canUnderstand = _language.CanUnderstand(Transform(uid).ParentUid, args.Language.ID);
            var msg = new MsgChatMessage
            {
                Message = canUnderstand ? args.OriginalChatMsg : args.LanguageObfuscatedChatMsg
            };
            _netMan.ServerSendMessage(msg, actor.PlayerSession.Channel);

            // Einstein Engines - Language end

            // Mono - Borers begin
            var ev = new RadioMessageHeardEvent(uid, msg, args.Channel);
            RaiseLocalEvent(Transform(uid).ParentUid, ref ev);
            // Mono - Borers end

            // Send radio noise event to client
            var radioNoiseEvent = new RadioNoiseEvent(GetNetEntity(uid), args.Channel.ID);
            RaiseNetworkEvent(radioNoiseEvent, actor.PlayerSession);
        }
    }
}
