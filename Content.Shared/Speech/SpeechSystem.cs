using Content.Shared._DeepLagoon.Speech;
using Content.Shared.Chat.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared.Speech
{
    public sealed class SpeechSystem : EntitySystem
    {
        public override void Initialize()
        {
            base.Initialize();

            SubscribeLocalEvent<SpeakAttemptEvent>(OnSpeakAttempt);
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
            SetBubbleStyle(entity, entity.Comp.BarkSettings.BubbleStyle);
        }

        public void CopyCharacterSpeech(Entity<SpeechComponent> source, Entity<SpeechComponent> target)
        {
            target.Comp.BarkSettings = source.Comp.BarkSettings.Sanitize();
            target.Comp.HasCharacterSpeech = source.Comp.HasCharacterSpeech;
            target.Comp.CharacterSpeechSounds = source.Comp.CharacterSpeechSounds;
            SetBubbleStyle(target, source.Comp.BubbleStyle);
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
        }
    }
}
