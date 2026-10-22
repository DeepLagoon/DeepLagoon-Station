using Content.Client.Atmos;
using Content.Shared._DeepLagoon.Mapping;
using Content.Shared.Atmos.Components;
using Content.Shared.Hands.Components;
using Robust.Client.Placement;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Mapping;

/// <summary>
/// Keeps the placement ghost in sync with the entry selected on the held mapper RCD. The ghost is what
/// makes the engine rotate the placement direction on the rotate key.
/// </summary>
public sealed partial class MapperRcdGhostSystem : EntitySystem
{
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IPlacementManager _placement = default!;
    [Dependency] private IPrototypeManager _protos = default!;
    [Dependency] private MapperRcdSystem _catalog = default!;

    private readonly string _placementMode = typeof(AlignMapperRcd).Name;
    private readonly string _pipePlacementMode = typeof(AlignAtmosPipeLayers).Name;

    /// <summary>
    /// Entry the current ghost was made for. The pipe ghost changes its own prototype with the layer under the
    /// cursor, so the placed prototype cannot be used to tell whether the ghost is up to date.
    /// </summary>
    private string? _ghostEntry;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var placerEntity = _placement.CurrentPermission?.MobUid;
        var placerIsMapper = HasComp<MapperRcdComponent>(placerEntity);

        // Another tool or the build menu owns the placement.
        if (_placement.Eraser || placerEntity != null && !placerIsMapper)
            return;

        if (!TryComp(_player.LocalEntity, out HandsComponent? hands) ||
            hands.ActiveHand?.HeldEntity is not { } held ||
            !TryComp(held, out MapperRcdComponent? rcd) ||
            rcd.SelectedEntry is not { } selected ||
            !_catalog.TryGetEntry(selected, out var entry) ||
            entry.Prototype == null)
        {
            if (placerIsMapper)
                _placement.Clear();

            _ghostEntry = null;
            return;
        }

        if (placerEntity == held && _ghostEntry == selected && _placement.IsActive)
            return;

        // Pipes use the stock layer-aware mode: the tile quarter under the cursor picks the pipe layer.
        var layered = entry is { Mode: MapperRcdMode.Entity, Slot: MapperRcdSlot.Pipe } &&
                      _protos.TryIndex<EntityPrototype>(entry.Prototype, out var proto) &&
                      proto.TryComp<AtmosPipeLayersComponent>(out _, EntityManager.ComponentFactory);

        _ghostEntry = selected;
        _placement.Clear();
        _placement.BeginPlacing(new PlacementInformation
        {
            MobUid = held,
            PlacementOption = layered ? _pipePlacementMode : _placementMode,
            EntityType = entry.Prototype,
            Range = (int) Math.Ceiling(rcd.Range),
            IsTile = entry.Mode == MapperRcdMode.Tile,
            UseEditorContext = false,
        });
    }
}
