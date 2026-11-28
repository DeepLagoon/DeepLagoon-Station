using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.Enums;
using Robust.Shared.Timing;
using Content.Shared.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Client.Player;

namespace Content.Client._DeepLagoon.BodyInsurance;

public sealed class BodyInsuranceBiteOverlay : Overlay
{
    private bool _bitePlayed;

    private readonly IEntityManager _entityManager =
        IoCManager.Resolve<IEntityManager>();

    private readonly IPlayerManager _playerManager =
        IoCManager.Resolve<IPlayerManager>();
    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    private readonly Texture _top;
    private readonly Texture _bottom;

    private float _elapsed = 2f;

    public const float Duration = 1.2f;

    public void Play()
    {
        _elapsed = 0f;
        _bitePlayed = false;
    }

    private float GetFlashAlpha()
    {
        // Быстрое усиление красного оттенка.
        if (_elapsed < 0.15f)
            return Smooth(_elapsed / 0.15f) * 0.22f;

        // Небольшое удержание.
        if (_elapsed < 0.45f)
            return 0.22f;

        // Плавное затухание до конца анимации.
        return 0.22f * (1f - Smooth((_elapsed - 0.45f) / 0.75f));
    }

    public BodyInsuranceBiteOverlay()
    {
        var resources = IoCManager.Resolve<IResourceCache>();

        _top = resources.GetResource<TextureResource>(
            "/Textures/_DeepLagoon/Interface/BodyInsurance/jaw_top.png"
        ).Texture;

        _bottom = resources.GetResource<TextureResource>(
            "/Textures/_DeepLagoon/Interface/BodyInsurance/jaw_bottom.png"
        ).Texture;
    }

    public void Stop()
    {
        _elapsed = Duration;
        _bitePlayed = false;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        if (_elapsed >= Duration)
            return;

        _elapsed += args.DeltaSeconds;

        if (!_bitePlayed && _elapsed >= 0.05f)
        {
            if (_playerManager.LocalEntity is not { } player)
                return;

            var audio = _entityManager
                .System<SharedAudioSystem>();

            audio.PlayPredicted(
                new SoundPathSpecifier(
                    "/Audio/Effects/demon_consume.ogg"),
                player,
                player,
                AudioParams.Default
                    .WithVolume(-3f)
                    .WithPitchScale(0.85f));

            _bitePlayed = true;
        }
    }

    private static float Smooth(float value)
    {
        value = Math.Clamp(value, 0f, 1f);
        return value * value * (3f - 2f * value);
    }

    private float GetClosure()
    {
        // Плавное появление клыков.
        if (_elapsed < 0.25f)
            return Smooth(_elapsed / 0.25f) * 0.35f;

        // Быстрое схлопывание.
        if (_elapsed < 0.45f)
            return 0.35f + Smooth(
                (_elapsed - 0.25f) / 0.20f) * 0.65f;

        // Короткая задержка после укуса.
        if (_elapsed < 0.60f)
            return 1f;

        // Плавное раскрытие.
        return 1f - Smooth(
            (_elapsed - 0.60f) / 0.60f);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_elapsed >= Duration)
            return;

        var handle = args.ScreenHandle;

        var flashAlpha = GetFlashAlpha();

        if (flashAlpha > 0f)
        {
            var flashColor = new Color(
               110,
                 0,
                 0,
                 (byte)(flashAlpha * 255));

            var viewport = args.ViewportBounds;

            var flashBox = new UIBox2(
                viewport.Left,
                viewport.Top,
                viewport.Right,
                viewport.Bottom);

            handle.DrawRect(flashBox, flashColor);
        }

        var bounds = new UIBox2(
            args.ViewportBounds.Left,
            args.ViewportBounds.Top,
            args.ViewportBounds.Right,
            args.ViewportBounds.Bottom);

        var width = (float)bounds.Width;
        var height = (float)bounds.Height;

        var jawHeight = height * 0.72f;

        var closure = GetClosure();

        var topY = -jawHeight +
                   jawHeight * closure;

        var bottomY = height -
                      jawHeight * closure;

        var topBox = new UIBox2(
            bounds.Left,
            bounds.Top + topY,
            bounds.Left + width,
            bounds.Top + topY + jawHeight);

        var bottomBox = new UIBox2(
            bounds.Left,
            bounds.Top + bottomY,
            bounds.Left + width,
            bounds.Top + bottomY + jawHeight);

        handle.DrawTextureRect(
            _top,
            topBox,
            Color.White);

        handle.DrawTextureRect(
            _bottom,
            bottomBox,
            Color.White);
    }
}
