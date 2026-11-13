using Content.Shared.Buckle.Components;
using Content.Shared.Rotation;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Shared.Animations;

namespace Content.Client.Rotation;

public sealed partial class RotationVisualizerSystem : SharedRotationVisualsSystem
{

    [Dependency] private AppearanceSystem _appearance = default!;
    [Dependency] private AnimationPlayerSystem _animation = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RotationVisualsComponent, AppearanceChangeEvent>(OnAppearanceChange);
    }

    private void OnAppearanceChange(EntityUid uid, RotationVisualsComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null)
            return;

        if (_appearance.TryGetData<bool>(uid, BuckleVisuals.Buckled, out var buckled, args.Component) && buckled)
            return;

        var hasState = _appearance.TryGetData<RotationState>(uid, RotationVisuals.RotationState, out var state, args.Component);
        var hasTilt = _appearance.TryGetData<int>(uid, RotationVisuals.Tilt, out var tilt, args.Component);
        if (!hasState && !hasTilt)
            return;

        var rotation = state == RotationState.Horizontal
            ? component.HorizontalRotation
            : component.VerticalRotation + Angle.FromDegrees(tilt * 15);
        AnimateSpriteRotation(uid, args.Sprite, rotation, component.AnimationTime);
    }

    /// <summary>
    ///     Rotates a sprite between two animated keyframes given a certain time.
    /// </summary>
    public void AnimateSpriteRotation(EntityUid uid, SpriteComponent spriteComp, Angle rotation, float animationTime)
    {
        const string animationKey = "rotate";
        if (TryComp<AnimationPlayerComponent>(uid, out var animationComp))
            _animation.Stop((uid, animationComp), animationKey);

        if (spriteComp.Rotation.Equals(rotation))
            return;

        animationComp ??= EnsureComp<AnimationPlayerComponent>(uid);

        var animation = new Animation
        {
            Length = TimeSpan.FromSeconds(animationTime),
            AnimationTracks =
            {
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Rotation),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(spriteComp.Rotation, 0),
                        new AnimationTrackProperty.KeyFrame(rotation, animationTime)
                    }
                }
            }
        };

        _animation.Play((uid, animationComp), animation, animationKey);
    }
}
