using Content.Shared._DeepLagoon.Speech;
using Content.Shared._DeepLagoon.DiscordLink;
using Content.Shared.Chat.Prototypes;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Shared.Speech
{
    public sealed class SpeechSystem : EntitySystem
    {
        [Dependency] private INetManager _net = default!;
        [Dependency] private ActorSystem _actors = default!;
        [Dependency] private SharedDiscordBoostSystem _supporters = default!;

        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<SpeakAttemptEvent>(OnSpeakAttempt);
            if (_net.IsServer)
            {
                SubscribeLocalEvent<SpeechComponent, PlayerAttachedEvent>(OnPlayerAttached);
                SubscribeLocalEvent<SpeechComponent, PlayerDetachedEvent>(OnPlayerDetached);
            }
        }

        public void SetSpeech(EntityUid uid, bool value, SpeechComponent? component = null)
        {
            if (value && !Resolve(uid, ref component))
                return;

            component = EnsureComp<SpeechComponent>(uid);

            if (component.Enabled == value)
                return;

            component.Enabled = value;

            Dirty(uid, component);
        }

        public void ApplyCharacterSpeech(Entity<SpeechComponent> entity, SpeechBarkSettings settings,
            ProtoId<SpeechSoundsPrototype>? speciesSounds)
        {
            entity.Comp.BarkSettings = settings.Sanitize();
            entity.Comp.HasCharacterSpeech = true;
            entity.Comp.CharacterSpeechSounds = speciesSounds;
            RefreshCharacterBubble(entity.Owner);
        }

        public void CopyCharacterSpeech(Entity<SpeechComponent> source, Entity<SpeechComponent> target)
        {
            target.Comp.BarkSettings = source.Comp.BarkSettings.Sanitize();
            target.Comp.HasCharacterSpeech = source.Comp.HasCharacterSpeech;
            target.Comp.CharacterSpeechSounds = source.Comp.CharacterSpeechSounds;
            RefreshCharacterBubble(target.Owner);
        }

        public void RefreshCharacterBubble(EntityUid uid)
        {
            if (!_net.IsServer || !TryComp<SpeechComponent>(uid, out var speech) || !speech.HasCharacterSpeech)
                return;

            var style = speech.BarkSettings.BubbleStyle;
            if (style != SpeechBubbleStyle.Default && !_supporters.CanCustomizeSpeechBubble(_actors.GetSession(uid)))
                style = SpeechBubbleStyle.Default;
            SetBubbleStyle((uid, speech), style);
        }

        private void OnPlayerAttached(EntityUid uid, SpeechComponent speech, PlayerAttachedEvent args) => RefreshCharacterBubble(uid);

        private void OnPlayerDetached(EntityUid uid, SpeechComponent speech, PlayerDetachedEvent args)
        {
            if (speech.HasCharacterSpeech)
                SetBubbleStyle((uid, speech), SpeechBubbleStyle.Default);
        }

        private void SetBubbleStyle(Entity<SpeechComponent> entity, SpeechBubbleStyle style)
        {
            if (entity.Comp.BubbleStyle == style)
                return;

            entity.Comp.BubbleStyle = style;
            Dirty(entity);
        }

        private void OnSpeakAttempt(SpeakAttemptEvent args)
        {
            if (!TryComp(args.Uid, out SpeechComponent? speech) || !speech.Enabled)
                args.Cancel();
            else
                RefreshCharacterBubble(args.Uid);
        }
    }
}
