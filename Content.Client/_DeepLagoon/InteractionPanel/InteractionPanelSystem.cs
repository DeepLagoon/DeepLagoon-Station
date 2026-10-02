using System.Linq;
using Content.Shared.Mobs.Components;
using Robust.Shared.Input.Binding;
using System.Numerics;
using Content.Shared.CCVar;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Shared.Animations;
using Robust.Shared.Configuration;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Player;
using Content.Shared._DeepLagoon.InteractionPanel;
using Content.Shared.GameTicking;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Timing;

namespace Content.Client._DeepLagoon.InteractionPanel;

public sealed class InteractionPanelSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly AnimationPlayerSystem _animations = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    private InteractionPanelWindow? _panel;
    private bool _replacingPanel;
    private readonly Dictionary<Guid, (DefaultWindow Window, TimeSpan Expires)> _requests = new();

    public override void Initialize()
    {
        CommandBinds.Builder
            .Bind(InteractionPanelKeyFunctions.OpenInteractions, new PointerInputCmdHandler(OpenFromPointer, outsidePrediction: true))
            .Register<InteractionPanelSystem>();
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(_ => CloseWindows());
        SubscribeNetworkEvent<InteractionPanelLibraryEvent>(args => _panel?.SetLibrary(args));
        SubscribeNetworkEvent<InteractionPanelPanelEvent>(OnPanel);
        SubscribeNetworkEvent<InteractionPanelAskEvent>(OnAsk);
        SubscribeNetworkEvent<InteractionPanelStatusEvent>(args => _panel?.SetStatus(args));
        SubscribeNetworkEvent<InteractionPanelAnimationEvent>(OnAnimation);
        SubscribeNetworkEvent<RoundRestartCleanupEvent>(_ => CloseWindows());
    }

    private bool OpenFromPointer(in PointerInputCmdHandler.PointerInputCmdArgs args)
    {
        if (!HasComp<MobStateComponent>(args.EntityUid)) return false;
        RaiseNetworkEvent(new InteractionPanelOpenEvent(GetNetEntity(args.EntityUid)));
        return true;
    }

    private void OnPanel(InteractionPanelPanelEvent args)
    {
        _replacingPanel = true;
        _panel?.Close();
        _replacingPanel = false;
        var target = GetEntity(args.Target);
        var window = new InteractionPanelWindow(args, Exists(target) ? target : null,
            _cfg.GetCVar(InteractionPanelCVars.Favorites).Split(',', StringSplitOptions.RemoveEmptyEntries),
            _cfg.GetCVar(InteractionPanelCVars.Interval));
        _panel = window;
        window.ActionRequested += id => RaiseNetworkEvent(new InteractionPanelActionEvent(args.Target, id));
        window.RepeatRequested += (id, enabled, interval) => RaiseNetworkEvent(new InteractionPanelRepeatEvent(args.Target, id, enabled, interval));
        window.PreferencesChanged += () => SavePreferences(window);
        window.OnClose += () =>
        {
            SavePreferences(window);
            if (!_replacingPanel) RaiseNetworkEvent(new InteractionPanelCloseEvent(args.Target));
            if (_panel == window) _panel = null;
        };
        window.Editor.SaveRequested += action => RaiseNetworkEvent(new InteractionPanelCustomSaveEvent(action));
        window.Editor.DeleteRequested += id => RaiseNetworkEvent(new InteractionPanelCustomDeleteEvent(id));
        window.OpenCentered();
        RaiseNetworkEvent(new InteractionPanelLibraryRequestEvent());
    }

    private void SavePreferences(InteractionPanelWindow window)
    {
        var favorites = string.Join(',', window.Favorites.OrderBy(x => x));
        if (_cfg.GetCVar(InteractionPanelCVars.Favorites) == favorites && _cfg.GetCVar(InteractionPanelCVars.Interval).Equals(window.Interval)) return;
        _cfg.SetCVar(InteractionPanelCVars.Favorites, favorites);
        _cfg.SetCVar(InteractionPanelCVars.Interval, window.Interval);
        _cfg.SaveToFile();
    }

    private void OnAnimation(InteractionPanelAnimationEvent args)
    {
        if (_cfg.GetCVar(CCVars.ReducedMotion)) return;
        var user = GetEntity(args.User);
        var target = GetEntity(args.Target);
        if (!TryComp<SpriteComponent>(user, out var sprite) || !Exists(target)) return;
        // Не перебиваем уже идущие анимации. Перемещается только изображение, не физическое тело.
        if (TryComp<AnimationPlayerComponent>(user, out var player) && player.PlayingAnimationCount > 0) return;
        var settings = args.Settings.Clamped();
        var direction = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(user);
        var offset = direction.LengthSquared() < 0.001f ? new Vector2(0, settings.SelfDistance) : Vector2.Normalize(direction) * settings.Distance;
        _animations.Play(user, new Animation
        {
            Length = TimeSpan.FromSeconds(settings.ForwardSeconds + settings.ReturnSeconds),
            AnimationTracks =
            {
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Offset),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(sprite.Offset, 0f),
                        new AnimationTrackProperty.KeyFrame(sprite.Offset + offset, settings.ForwardSeconds),
                        new AnimationTrackProperty.KeyFrame(sprite.Offset, settings.ReturnSeconds),
                    }
                }
            }
        }, "deeplagoon-interaction-panel");
    }

    private void OnAsk(InteractionPanelAskEvent args)
    {
        if (_requests.ContainsKey(args.Token)) return;
        var window = new DefaultWindow { Title = Loc.GetString("dl-interaction-panel-request-title"), MinWidth = 420 };
        _requests[args.Token] = (window, _timing.CurTime + TimeSpan.FromSeconds(20));
        var answered = false;
        var content = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical, Margin = new Thickness(8) };
        content.AddChild(new Label { Text = Loc.GetString("dl-interaction-panel-request", ("name", args.Name)) });
        content.AddChild(new Label { Text = args.Title ?? Loc.GetString($"dl-interaction-panel-action-{args.Action}") });
        if (args.Text != null)
        {
            var preview = new RichTextLabel { MaxWidth = 460 };
            preview.SetMessage(Robust.Shared.Utility.FormattedMessage.EscapeText(args.Text));
            content.AddChild(preview);
        }
        content.AddChild(new Label { Text = Loc.GetString("dl-interaction-panel-request-timeout") });
        var buttons = new BoxContainer();
        foreach (var accepted in new[] { true, false })
        {
            var button = new Button { Text = Loc.GetString(accepted ? "dl-interaction-panel-accept" : "dl-interaction-panel-decline"), HorizontalExpand = true };
            button.OnPressed += _ =>
            {
                answered = true;
                RaiseNetworkEvent(new InteractionPanelAnswerEvent(args.Token, accepted));
                window.Close();
            };
            buttons.AddChild(button);
        }
        var block = new Button { Text = Loc.GetString("dl-interaction-panel-ask-block") };
        block.OnPressed += _ =>
        {
            answered = true;
            RaiseNetworkEvent(new InteractionPanelAnswerEvent(args.Token, false, true));
            window.Close();
        };
        content.AddChild(block);
        content.AddChild(buttons);
        window.Contents.AddChild(content);
        window.OnClose += () =>
        {
            if (!answered) RaiseNetworkEvent(new InteractionPanelAnswerEvent(args.Token, false));
            _requests.Remove(args.Token);
        };
        window.OpenCentered();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var request in _requests.Values.ToArray())
            if (request.Expires <= _timing.CurTime) request.Window.Close();
    }

    private void CloseWindows()
    {
        _panel?.Close();
        foreach (var request in _requests.Values.ToArray()) request.Window.Close();
    }

    public override void Shutdown()
    {
        CommandBinds.Unregister<InteractionPanelSystem>();
        CloseWindows();
        base.Shutdown();
    }
}
