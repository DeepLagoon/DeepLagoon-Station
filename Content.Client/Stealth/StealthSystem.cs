using System.Numerics;
using Content.Client.Interactable.Components;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Client.Stealth;

public sealed partial class StealthSystem : SharedStealthSystem
{
    [Dependency] private IPrototypeManager _protoMan = default!;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private TransformSystem _transformSystem = default!;

    private const string StealthPass = "stealth";
    private static readonly ProtoId<ShaderPrototype> StealthShader = "Stealth";
    private ShaderInstance _shader = default!;

    public override void Initialize()
    {
        base.Initialize();

        _shader = _protoMan.Index(StealthShader).InstanceUnique();

        SubscribeLocalEvent<StealthComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<StealthComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<StealthComponent, AfterAutoHandleStateEvent>(OnAfterState);
        SubscribeLocalEvent<StealthComponent, BeforePostShaderRenderEvent>(OnShaderRender);
    }

    public override void SetEnabled(EntityUid uid, bool value, StealthComponent? component = null)
    {
        if (!Resolve(uid, ref component) || component.Enabled == value)
            return;

        base.SetEnabled(uid, value, component);
        SetShader(uid, value, component);
    }

    private void SetShader(EntityUid uid, bool enabled, StealthComponent component)
    {
        if (!TryComp(uid, out SpriteComponent? sprite) || _sprites.HasPostShader(sprite, StealthPass) == enabled)
            return;

        _sprites.SetColor((uid, sprite), Color.White);

        if (!enabled)
        {
            _sprites.RemovePostShader(sprite, StealthPass);
            if (component.HadOutline && !TerminatingOrDeleted(uid))
                EnsureComp<InteractionOutlineComponent>(uid);
            return;
        }

        if (TryComp(uid, out InteractionOutlineComponent? outline))
        {
            outline.OnMouseLeave(uid);
            RemCompDeferred(uid, outline);
            component.HadOutline = true;
        }

        _sprites.SetPostShader(sprite, new SpriteComponent.PostShaderArgs(StealthPass, _shader)
        {
            GetScreenTexture = true,
            RaiseShaderEvent = true,
        });
    }

    private void OnStartup(EntityUid uid, StealthComponent component, ComponentStartup args)
    {
        SetShader(uid, component.Enabled, component);
    }

    private void OnShutdown(EntityUid uid, StealthComponent component, ComponentShutdown args)
    {
        if (!Terminating(uid))
            SetShader(uid, false, component);
    }

    private void OnAfterState(EntityUid uid, StealthComponent component, ref AfterAutoHandleStateEvent args)
    {
        SetShader(uid, component.Enabled, component);
    }

    private void OnShaderRender(EntityUid uid, StealthComponent component, ref BeforePostShaderRenderEvent args)
    {
        if (args.Id != StealthPass)
            return;

        // Anchor the distortion to the parent's screen position so camera movement doesn't shift the pattern.
        var parent = Transform(uid).ParentUid;
        var reference = Vector2.Zero;
        if (parent.IsValid())
        {
            reference = args.Viewport.WorldToLocal(_transformSystem.GetRenderWorldPosition(parent));
            reference.X = -reference.X;
        }

        var visibility = Math.Clamp(GetVisibility(uid, component), -1.5f, 1f);
        var size = args.Viewport.Size;
        args.Shader.SetParameter("viewport_pixel_size", new Vector2(1f / size.X, 1f / size.Y));
        args.Shader.SetParameter("reference", reference);
        args.Shader.SetParameter("visibility", visibility);

        visibility = MathF.Max(0, visibility);
        _sprites.SetColor((uid, args.Sprite), new Color(visibility, visibility, 1, 1));
    }
}
