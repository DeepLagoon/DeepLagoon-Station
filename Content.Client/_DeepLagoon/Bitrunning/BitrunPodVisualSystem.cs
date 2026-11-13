using Content.Shared._DeepLagoon.Bitrunning;
using Content.Shared.Buckle.Components;
using Content.Shared.DragDrop;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Systems;
using Content.Shared.Power;
using Content.Shared.Wires;
using Robust.Client.Animations;
using Robust.Client.GameObjects;
using Robust.Shared.Containers;

namespace Content.Client._DeepLagoon.Bitrunning;

public sealed partial class BitrunPodVisualSystem : VisualizerSystem<BitrunPodComponent>
{
    private const string DoorLayer = "pod";
    private const string DoorAnimation = "bitrun-pod-door";

    [Dependency] private AnimationPlayerSystem _animations = default!;
    [Dependency] private SpriteSystem _sprites = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private SharedContainerSystem _containers = default!;

    private readonly Dictionary<EntityUid, (bool Powered, bool PanelOpen, bool Occupied)> _appearanceStates = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<BitrunPodComponent, ComponentShutdown>(OnPodShutdown);
        SubscribeLocalEvent<BitrunPodComponent, CanDropTargetEvent>(OnCanDrop);
    }

    protected override void OnAppearanceChange(EntityUid uid, BitrunPodComponent component, ref AppearanceChangeEvent args)
    {
        if (args.Sprite is not { } sprite)
            return;

        var occupied = AppearanceSystem.TryGetData<bool>(uid, BitrunMachineVisuals.Occupied, out var hasOccupant, args.Component) && hasOccupant;
        var powered = AppearanceSystem.TryGetData<bool>(uid, PowerDeviceVisuals.Powered, out var hasPower, args.Component) && hasPower;
        var panelOpen = AppearanceSystem.TryGetData<bool>(uid, WiresVisuals.MaintenancePanelState, out var open, args.Component) && open;
        var state = (Powered: powered, PanelOpen: panelOpen, Occupied: occupied);
        var restingState = !powered ? "netpod" : panelOpen ? "netpod_panel" :
            occupied ? "netpod_closed_active" : "netpod_open_active";

        var hadState = _appearanceStates.TryGetValue(uid, out var previous);
        if (hadState && previous == state)
            return;

        _appearanceStates[uid] = state;
        if (_animations.HasRunningAnimation(uid, DoorAnimation))
            _animations.Stop(uid, DoorAnimation);

        if (!hadState || !powered || panelOpen || !previous.Powered || previous.PanelOpen)
        {
            _sprites.LayerSetRsiState((uid, sprite), DoorLayer, restingState);
            return;
        }

        // The imported states each contain 17 frames of 0.1 seconds.
        _animations.Play(uid, new Animation
        {
            Length = TimeSpan.FromSeconds(1.75),
            AnimationTracks =
            {
                new AnimationTrackSpriteFlick
                {
                    LayerKey = DoorLayer,
                    KeyFrames =
                    {
                        new AnimationTrackSpriteFlick.KeyFrame(occupied ? "netpod_closing" : "netpod_opening", 0f),
                        new AnimationTrackSpriteFlick.KeyFrame(restingState, 1.7f),
                    },
                },
            },
        }, DoorAnimation);
    }

    private void OnCanDrop(Entity<BitrunPodComponent> pod, ref CanDropTargetEvent args)
    {
        if (args.Handled || !HasComp<HumanoidAppearanceComponent>(args.Dragged))
            return;

        args.Handled = true;
        args.CanDrop = Transform(pod).Anchored && _mobState.IsAlive(args.Dragged) &&
            !_containers.IsEntityInContainer(args.Dragged) &&
            !(TryComp<BuckleComponent>(args.Dragged, out var buckle) && buckle.Buckled) &&
            _appearanceStates.TryGetValue(pod, out var state) && state.Powered && !state.Occupied;
    }

    private void OnPodShutdown(Entity<BitrunPodComponent> pod, ref ComponentShutdown args)
    {
        _appearanceStates.Remove(pod.Owner);
    }
}
