using System.Linq;
using System.Numerics;
using Content.Client.Shuttles.UI;
using Content.Shared._Mono.FireControl;
using Content.Shared.Physics;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Shuttles.Systems;
using Content.Client._Mono.Radar;
using Content.Shared._Mono.Radar;
using Content.Shared._Crescent.ShipShields;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Client._Mono.FireControl.UI;

public class FireControlNavControl : ShuttleNavControl
{
    private readonly SharedTransformSystem _transform;
    private readonly SharedPhysicsSystem _physics;
    private readonly RadarBlipsSystem _blips;

    private EntityUid? _activeConsole;
    private FireControllableEntry[]? _controllables;
    private HashSet<NetEntity> _selectedWeapons = new();
    private Vector2[] _destroyedWeapons = Array.Empty<Vector2>();
    private readonly List<(NetEntity? Entity, Vector2 Position)> _weaponMarkers = new();
    private bool _weaponClick;
    public event Action<NetEntity>? OnWeaponSelectionRequested;

    private const float WeaponMarkerRadius = 5f;
    private const float WeaponHitRadius = 8f;

    private readonly Dictionary<NetEntity, Color> _blipColors = new();

    // Add a limit to how often we update the cursor position to prevent network spam
    private float _lastCursorUpdateTime = 0f;
    private const float CursorUpdateInterval = 0.1f; // 10 updates per second

    public FireControlNavControl() : base(64f, 512f, 512f)
    {
        IoCManager.InjectDependencies(this);
        _blips = EntManager.System<RadarBlipsSystem>();
        _physics = EntManager.System<SharedPhysicsSystem>();
        _transform = EntManager.System<SharedTransformSystem>();
    }

    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        if (_isMouseInside)
            // Continuously update the cursor position for guided missiles
            TryUpdateCursorPosition(_lastMousePos);
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        _weaponMarkers.Clear();
        if (_coordinates == null || _rotation == null)
            return;

        var xformQuery = EntManager.GetEntityQuery<TransformComponent>();
        if (!xformQuery.TryGetComponent(_coordinates.Value.EntityId, out var xform)
            || xform.MapID == MapId.Nullspace)
        {
            return;
        }

        base.Draw(handle);

        var coordEntRot = _transform.GetWorldRotation(_coordinates.Value.EntityId);

        var worldRot = _rotation.Value;

        var mapPos = _transform.ToMapCoordinates(_coordinates.Value).Offset(_rotation.Value.RotateVec(Offset));
        var mapCoord = _transform.ToCoordinates(mapPos);
        var worldToShuttle = Matrix3Helpers.CreateTranslation(-mapCoord.Position) * Matrix3Helpers.CreateRotation(-worldRot);
        Matrix3x2.Invert(worldToShuttle, out var shuttleToWorld);
        var shuttleToView = Matrix3x2.CreateScale(new Vector2(MinimapScale, -MinimapScale)) * Matrix3x2.CreateTranslation(MidPointVector);
        var worldToView = worldToShuttle * shuttleToView;
        Matrix3x2.Invert(worldToView, out var viewToWorld);

        var blips = _blips.GetCurrentBlips();
        _blipColors.Clear();
        foreach (var blip in blips)
            _blipColors[blip.NetUid] = blip.Config.Color;

        if (_controllables != null)
        {
            foreach (var controllable in _controllables)
            {
                var coords = EntManager.GetCoordinates(controllable.Coordinates);
                var worldPos = _transform.ToMapCoordinates(coords).Position;

                if (_selectedWeapons.Contains(controllable.NetEntity))
                {
                    var cursorViewPos = InverseScalePosition(_lastMousePos);
                    cursorViewPos = ScalePosition(cursorViewPos);

                    var cursorWorldPos = Vector2.Transform(cursorViewPos, viewToWorld);

                    var direction = cursorWorldPos - worldPos;
                    var ray = new CollisionRay(worldPos, direction.Normalized(), (int)CollisionGroup.Impassable);

                    var results = _physics.IntersectRay(xform.MapID, ray, direction.Length(), ignoredEnt: _coordinates?.EntityId);

                    if ((!results.Any() || controllable.IgnoresLos) && _blipColors.TryGetValue(controllable.NetEntity, out var color))
                        handle.DrawLine(Vector2.Transform(worldPos, worldToView), cursorViewPos, color.WithAlpha(0.3f));
                }
            }
        }

        handle.UseShader(null);
        if (_activeConsole is { } console && EntManager.TryGetComponent<TransformComponent>(console, out var consoleXform)
            && consoleXform.GridUid is { } weaponGrid)
        {
            foreach (var position in _destroyedWeapons)
            {
                var worldPos = _transform.ToMapCoordinates(new EntityCoordinates(weaponGrid, position)).Position;
                DrawWeaponMarker(handle, Vector2.Transform(worldPos, worldToView), null, Color.Red);
            }
        }
        if (_controllables == null)
            return;
        foreach (var weapon in _controllables)
        {
            var worldPos = _transform.ToMapCoordinates(EntManager.GetCoordinates(weapon.Coordinates)).Position;
            DrawWeaponMarker(handle, Vector2.Transform(worldPos, worldToView), weapon.NetEntity,
                _selectedWeapons.Contains(weapon.NetEntity) ? Color.Cyan : Color.Yellow);
        }
    }

    private void DrawWeaponMarker(DrawingHandleScreen handle, Vector2 position, NetEntity? entity, Color color)
    {
        if (!new UIBox2(Vector2.Zero, PixelSize).Contains(position))
            return;
        _weaponMarkers.Add((entity, position));
        var extent = new Vector2(WeaponMarkerRadius * UIScale);
        handle.DrawRect(new UIBox2(position - extent, position + extent), Color.Black.WithAlpha(0.8f));
        handle.DrawRect(new UIBox2(position - extent, position + extent), color, filled: false);
        if (entity == null)
        {
            handle.DrawLine(position - extent, position + extent, color);
            handle.DrawLine(position + new Vector2(-extent.X, extent.Y), position + new Vector2(extent.X, -extent.Y), color);
        }
        else if (_selectedWeapons.Contains(entity.Value))
            handle.DrawRect(new UIBox2(position - extent / 2, position + extent / 2), color);
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        _weaponClick = false;
        if (args.Function == EngineKeyFunctions.UIClick && TrySelectWeapon(args.RelativePosition))
        {
            _weaponClick = true;
            _isMouseDown = false;
            args.Handle();
            return;
        }
        base.KeyBindDown(args);
    }

    protected override void KeyBindUp(GUIBoundKeyEventArgs args)
    {
        if (args.Function == EngineKeyFunctions.UIClick && _weaponClick)
        {
            _weaponClick = false;
            _isMouseDown = false;
            args.Handle();
            return;
        }
        base.KeyBindUp(args);
    }

    public bool TrySelectWeapon(Vector2 relativePosition)
    {
        var distance = WeaponHitRadius * UIScale;
        var nearest = distance * distance;
        (NetEntity? Entity, Vector2 Position)? hit = null;
        foreach (var marker in _weaponMarkers)
        {
            var squared = Vector2.DistanceSquared(relativePosition * UIScale, marker.Position);
            if (squared > nearest || squared == nearest && hit?.Entity != null)
                continue;
            nearest = squared;
            hit = marker;
        }
        if (hit == null)
            return false;
        if (hit.Value.Entity is { } entity && _controllables?.Any(weapon => weapon.NetEntity == entity) == true)
            OnWeaponSelectionRequested?.Invoke(entity);
        return true;
    }

    public void UpdateDestroyedWeapons(Vector2[] positions) => _destroyedWeapons = positions;

    public void UpdateControllables(EntityUid console, FireControllableEntry[] controllables)
    {
        _activeConsole = console;
        _controllables = controllables;
        _selectedWeapons.RemoveWhere(entity => !controllables.Any(weapon => weapon.NetEntity == entity));
    }

    public void UpdateSelectedWeapons(HashSet<NetEntity> selectedWeapons)
    {
        _selectedWeapons = selectedWeapons;
    }

    private void TryUpdateCursorPosition(Vector2 relativePosition)
    {
        var currentTime = IoCManager.Resolve<IGameTiming>().CurTime.TotalSeconds;
        if (currentTime - _lastCursorUpdateTime < CursorUpdateInterval)
            return;

        _lastCursorUpdateTime = (float)currentTime;

        var coords = GetMouseEntityCoordinates(relativePosition);
        // This will update the server of our cursor position without triggering actual firing
        OnRadarClick?.Invoke(coords);
    }

    /// <summary>
    /// Returns true if the mouse button is currently pressed down
    /// </summary>
    public bool IsMouseDown() => _isMouseDown;
}
