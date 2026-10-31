using Content.Shared.Audio.Jukebox;
using Content.Shared.Item;
using Content.Shared.Toggleable;
using Robust.Shared.Audio.Components;
using Robust.Shared.GameObjects;

namespace Content.Server._DeepLagoon.Audio;

[RegisterComponent]
public sealed partial class PortableJukeboxComponent : Component
{
    public bool Playing;
}

/// <summary>Animate the portable speaker and both inhands only while its audio plays.</summary>
public sealed class PortableJukeboxSystem : EntitySystem
{
    [Dependency] private SharedItemSystem _items = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    private float _elapsed;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        _elapsed += frameTime;
        if (_elapsed < 0.2f)
            return;
        _elapsed = 0;

        var query = EntityQueryEnumerator<PortableJukeboxComponent, JukeboxComponent>();
        while (query.MoveNext(out var uid, out var portable, out var jukebox))
        {
            var playing = TryComp<AudioComponent>(jukebox.AudioStream, out var audio) &&
                audio.State == AudioState.Playing;
            if (portable.Playing == playing)
                continue;
            portable.Playing = playing;
            _appearance.SetData(uid, ToggleableVisuals.Enabled, playing);
            _items.SetHeldPrefix(uid, playing ? "playing" : "idle");
        }
    }
}
