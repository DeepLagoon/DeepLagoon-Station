using Content.Shared.Storage.Components; // Frontier: Server<Shared
using Content.Shared.Examine;
using Content.Shared.Hands.Components;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle; // DeltaV
using Content.Shared.Whitelist;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using Content.Shared.Item; // Frontier
using Content.Shared.Verbs; // Frontier
using System.Linq;
using Content.Shared.Materials;
using Content.Shared.Materials.OreSilo;
using Content.Shared.Tag;
using Robust.Shared.Prototypes;

namespace Content.Shared.Storage.EntitySystems;

/// <summary>
/// <see cref="MagnetPickupComponent"/>
/// </summary>
public sealed partial class MagnetPickupSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private InventorySystem _inventory = default!;
    [Dependency] private ItemToggleSystem _toggle = default!; // DeltaV
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private SharedStorageSystem _storage = default!;
    [Dependency] private EntityWhitelistSystem _whitelistSystem = default!;
    [Dependency] private SharedItemSystem _item = default!; // Frontier
    [Dependency] private SharedOreSiloSystem _oreSilo = default!;
    [Dependency] private SharedMaterialStorageSystem _materialStorage = default!;
    [Dependency] private TagSystem _tag = default!;

    private static readonly ProtoId<TagPrototype> OreTag = "Ore";
    private static readonly ProtoId<TagPrototype> ArtifactFragmentTag = "ArtifactFragment";


    private static readonly TimeSpan ScanDelay = TimeSpan.FromSeconds(1);
    private const int MaxEntitiesToInsert = 15; // Frontier

    private EntityQuery<PhysicsComponent> _physicsQuery;

    public override void Initialize()
    {
        base.Initialize();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();
        SubscribeLocalEvent<MagnetPickupComponent, MapInitEvent>(OnMagnetMapInit);
        SubscribeLocalEvent<MagnetPickupComponent, ExaminedEvent>(OnExamined); // Frontier
        SubscribeLocalEvent<MagnetPickupComponent, GetVerbsEvent<AlternativeVerb>>(AddToggleMagnetVerb); // Frontier
    }

    private bool IsTransferableOre(EntityUid uid)
    {
        return _tag.HasTag(uid, OreTag)
            && !_tag.HasTag(uid, ArtifactFragmentTag);
    }

    private void OnMagnetMapInit(EntityUid uid, MagnetPickupComponent component, MapInitEvent args)
    {
        component.NextScan = _timing.CurTime;
    }


    // Frontier: togglable magnets
    private void AddToggleMagnetVerb(EntityUid uid, MagnetPickupComponent component, GetVerbsEvent<AlternativeVerb> args)
    {
        // Magnet run by other means (e.g. toggles)
        if (!component.MagnetCanBeEnabled)
            return;

        if (!args.CanAccess || !args.CanInteract)
            return;

        if (!HasComp<HandsComponent>(args.User))
            return;

        AlternativeVerb verb = new()
        {
            Act = () =>
            {
                ToggleMagnet(uid, component);
            },
            Icon = new SpriteSpecifier.Texture(new("/Textures/Interface/VerbIcons/Spare/poweronoff.svg.192dpi.png")),
            Text = Loc.GetString(component.MagnetEnabled
               ? "magnet-pickup-component-disable-verb"
               : "magnet-pickup-component-enable-verb"),
            Priority = component.MagnetTogglePriority // Frontier: 3 < component.MagnetTogglePriority
        };

        args.Verbs.Add(verb);
    }

    // Show the magnet state on examination
    private void OnExamined(EntityUid uid, MagnetPickupComponent component, ExaminedEvent args)
    {
        // Magnet run by other means (e.g. toggles)
        if (!component.MagnetCanBeEnabled)
            return;

        args.PushMarkup(Loc.GetString("magnet-pickup-component-on-examine-main",
                        ("stateText", Loc.GetString(component.MagnetEnabled
                        ? "magnet-pickup-component-magnet-on"
                        : "magnet-pickup-component-magnet-off"))));
    }

    //Toggles the magnet on the ore bag/box
    public void ToggleMagnet(EntityUid uid, MagnetPickupComponent comp)
    {
        // Magnet run by other means (e.g. toggles)
        if (!comp.MagnetCanBeEnabled)
            return;

        comp.MagnetEnabled = !comp.MagnetEnabled;
        Dirty(uid, comp);
    }
    // End Frontier: togglable magnets


    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<
            MagnetPickupComponent,
            StorageComponent,
            TransformComponent,
            MetaDataComponent>();

        var currentTime = _timing.CurTime;

        while (query.MoveNext(out var uid, out var comp,
                   out var storage, out var xform, out var meta))
        {
            if (comp.NextScan > currentTime)
                continue;

            comp.NextScan = currentTime + ScanDelay;

            if (comp.MagnetCanBeEnabled)
            {
                if (!comp.MagnetEnabled)
                    continue;
            }
            else
            {
                if (!_toggle.IsActivated(uid))
                    continue;
            }

            // DeepLagoon: resolve the linked material silo.
            var silo = _oreSilo.GetAvailableSilo(uid);
            var count = 0;

            // Transfer ore already stored inside the magnet.
            if (silo is { } connectedSilo)
            {
                foreach (var stored in
                         storage.Container.ContainedEntities.ToArray())
                {
                    if (count >= MaxEntitiesToInsert)
                        break;

                    if (!IsTransferableOre(stored))
                        continue;

                    if (!_materialStorage.TryInsertMaterialEntity(
                            connectedSilo, stored, connectedSilo))
                        continue;

                    count++;
                }
            }

            if (count >= MaxEntitiesToInsert)
                continue;

            var slotCount = _storage.GetCumulativeItemAreas(
                (uid, storage));

            var totalSlots = storage.Grid.GetArea();

            // A connected magnet can collect ore even if its
            // internal storage is full.
            if (slotCount >= totalSlots && silo == null)
                continue;

            var parentUid = xform.ParentUid;
            var playedSound = false;
            var finalCoords = xform.Coordinates;
            var moverCoords = _transform.GetMoverCoordinates(uid, xform);

            foreach (var near in _lookup.GetEntitiesInRange(
                         uid, comp.Range,
                         LookupFlags.Dynamic | LookupFlags.Sundries))
            {
                if (count >= MaxEntitiesToInsert)
                    break;

                if (near == parentUid)
                    continue;

                if (!_physicsQuery.TryGetComponent(near, out var physics)
                    || physics.BodyStatus != BodyStatus.OnGround)
                    continue;

                if (_whitelistSystem.IsWhitelistFail(storage.Whitelist, near))
                    continue;

                // DeepLagoon: send ore directly to the linked silo.
                if (silo is { } targetSilo && IsTransferableOre(near))
                {
                    if (_materialStorage.TryInsertMaterialEntity(
                            targetSilo, near, targetSilo))
                    {
                        count++;
                        continue;
                    }
                }

                // Normal storage for artifact fragments and
                // ore that could not be transferred.
                if (!TryComp<ItemComponent>(near, out var item))
                    continue;

                var itemSize = _item.GetItemShape((near, item)).GetArea();

                if (itemSize > totalSlots - slotCount)
                {
                    if (silo != null)
                        continue;

                    break;
                }

                count++;

                var nearXform = Transform(near);
                var nearMap = _transform.GetMapCoordinates(
                    near, xform: nearXform);

                var nearCoords = _transform.ToCoordinates(
                    moverCoords.EntityId, nearMap);

                if (!_storage.Insert(uid, near, out var stacked,
                        storageComp: storage,
                        playSound: !playedSound))
                {
                    if (silo != null)
                        continue;

                    break;
                }

                slotCount += itemSize;

                if (stacked != null)
                {
                    _storage.PlayPickupAnimation(
                        stacked.Value, nearCoords,
                        finalCoords, nearXform.LocalRotation);
                }
                else
                {
                    _storage.PlayPickupAnimation(
                        near, nearCoords,
                        finalCoords, nearXform.LocalRotation);
                }

                playedSound = true;
            }
        }
    }

}
