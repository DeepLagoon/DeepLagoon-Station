using Content.Client.UserInterface.Systems.Guidebook;
using Robust.Client.UserInterface;
using System.Linq;
using Content.Client.Guidebook.Components;
using Content.Client.Light;
using Content.Client.Verbs;
using Content.Shared.Guidebook;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Speech;
using Content.Shared.Tag;
using Content.Shared.Verbs;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.Guidebook;

/// <summary>
///     This system handles the help-verb and interactions with various client-side entities that are embedded into guidebooks.
/// </summary>
public sealed partial class GuidebookSystem : EntitySystem
{
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPlayerManager _playerManager = default!;
    [Dependency] private SharedAudioSystem _audioSystem = default!;
    [Dependency] private VerbSystem _verbSystem = default!;
    [Dependency] private RgbLightControllerSystem _rgbLightControllerSystem = default!;
    [Dependency] private SharedPointLightSystem _pointLightSystem = default!;
    [Dependency] private TagSystem _tags = default!;

    public event Action<List<ProtoId<GuideEntryPrototype>>,
        List<ProtoId<GuideEntryPrototype>>?,
        ProtoId<GuideEntryPrototype>?,
        bool,
        ProtoId<GuideEntryPrototype>?>? OnGuidebookOpen;

    public const string GuideEmbedTag = "GuideEmbeded";

    private EntityUid _defaultUser;

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<MetaDataComponent, GetVerbsEvent<ExamineVerb>>(OnGetVerbs);
        SubscribeLocalEvent<MetaDataComponent, ActivateInWorldEvent>(OnInteract,
            before: new[] { typeof(Content.Shared.UserInterface.ActivatableUISystem) });

        SubscribeLocalEvent<GuidebookControlsTestComponent, InteractHandEvent>(OnGuidebookControlsTestInteractHand);
        SubscribeLocalEvent<GuidebookControlsTestComponent, ActivateInWorldEvent>(OnGuidebookControlsTestActivateInWorld,
            before: new[] { typeof(Content.Shared.UserInterface.ActivatableUISystem) });
        SubscribeLocalEvent<GuidebookControlsTestComponent, GetVerbsEvent<AlternativeVerb>>(
            OnGuidebookControlsTestGetAlternateVerbs);
    }

    /// <summary>
    /// Gets a user entity to use for verbs and examinations. If the player has no attached entity, this will use a
    /// dummy client-side entity so that users can still use the guidebook when not attached to anything (e.g., in the
    /// lobby)
    /// </summary>
    public EntityUid GetGuidebookUser()
    {
        var user = _playerManager.LocalEntity;
        if (user != null)
            return user.Value;

        if (!Exists(_defaultUser))
            _defaultUser = Spawn(null, MapCoordinates.Nullspace);

        return _defaultUser;
    }

    private void OnGetVerbs(EntityUid uid, MetaDataComponent metadata, GetVerbsEvent<ExamineVerb> args)
    {
        TryComp<GuideHelpComponent>(uid, out var component);
        var controller = _ui.GetUIController<GuidebookUIController>();
        if (_tags.HasTag(uid, GuideEmbedTag) ||
            (component?.Guides.Count is not > 0 && !controller.HasEntityRoute(uid)))
            return;

        args.Verbs.Add(new()
        {
            Text = Loc.GetString("guide-help-verb"),
            Icon = new SpriteSpecifier.Texture(new ("/Textures/Interface/VerbIcons/information.svg.192dpi.png")),
            Act = () => controller.OpenEntityHelp(uid, component?.Guides.Select(id => id.ToString())),
            ClientExclusive = true,
            CloseMenu = true
        });
    }

    public void OpenHelp(List<ProtoId<GuideEntryPrototype>> guides)
    {
        if (guides.Count != 0) OnGuidebookOpen?.Invoke(guides, null, null, true, guides[0]);
    }

    private void OnInteract(EntityUid uid, MetaDataComponent metadata, ActivateInWorldEvent args)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        if (args.Handled || _tags.HasTag(uid, GuideEmbedTag))
            return;

        var controller = _ui.GetUIController<GuidebookUIController>();
        if (controller.HasEntityRoute(uid, "activation"))
        {
            args.Handled = controller.OpenEntityHelp(uid, prefix: "activation");
            return;
        }
        if (!TryComp<GuideHelpComponent>(uid, out var component) || !component.OpenOnActivation || component.Guides.Count == 0)
            return;
        args.Handled = controller.OpenEntityHelp(uid, component.Guides.Select(id => id.ToString()));
    }

    private void OnGuidebookControlsTestGetAlternateVerbs(EntityUid uid, GuidebookControlsTestComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        args.Verbs.Add(new AlternativeVerb()
        {
            Act = () =>
            {
                if (Transform(uid).LocalRotation != Angle.Zero)
                    Transform(uid).LocalRotation -= Angle.FromDegrees(90);
            },
            Text = Loc.GetString("guidebook-monkey-unspin"),
            Priority = -9999,
        });

        args.Verbs.Add(new AlternativeVerb()
        {
            Act = () =>
            {
                EnsureComp<PointLightComponent>(uid); // RGB demands this.
                _pointLightSystem.SetEnabled(uid, false);
                var rgb = EnsureComp<RgbLightControllerComponent>(uid);

                var sprite = EnsureComp<SpriteComponent>(uid);
                var layers = new List<int>();

                for (var i = 0; i < sprite.AllLayers.Count(); i++)
                {
                    layers.Add(i);
                }

                _rgbLightControllerSystem.SetLayers(uid, layers, rgb);
            },
            Text = Loc.GetString("guidebook-monkey-disco"),
            Priority = -9998,
        });
    }

    private void OnGuidebookControlsTestActivateInWorld(EntityUid uid, GuidebookControlsTestComponent component, ActivateInWorldEvent args)
    {
        Transform(uid).LocalRotation += Angle.FromDegrees(90);
    }

    private void OnGuidebookControlsTestInteractHand(EntityUid uid, GuidebookControlsTestComponent component, InteractHandEvent args)
    {
        if (!TryComp<SpeechComponent>(uid, out var speech) || speech.SpeechSounds is null)
            return;

        // This code is broken because SpeechSounds isn't a file name or sound specifier directly.
        // Commenting out to avoid compile failure with https://github.com/space-wizards/RobustToolbox/pull/5540
        // _audioSystem.PlayGlobal(speech.SpeechSounds, Filter.Local(), false, speech.AudioParams);
    }

    public void FakeClientActivateInWorld(EntityUid activated)
    {
        var activateMsg = new ActivateInWorldEvent(GetGuidebookUser(), activated, true);
        RaiseLocalEvent(activated, activateMsg);
    }

    public void FakeClientAltActivateInWorld(EntityUid activated)
    {
        // Get list of alt-interact verbs
        var verbs = _verbSystem.GetLocalVerbs(activated, GetGuidebookUser(), typeof(AlternativeVerb), force: true);

        if (!verbs.Any())
            return;

        _verbSystem.ExecuteVerb(verbs.First(), GetGuidebookUser(), activated);
    }

    public void FakeClientUse(EntityUid activated)
    {
        var activateMsg = new InteractHandEvent(GetGuidebookUser(), activated);
        RaiseLocalEvent(activated, activateMsg);
    }
}
