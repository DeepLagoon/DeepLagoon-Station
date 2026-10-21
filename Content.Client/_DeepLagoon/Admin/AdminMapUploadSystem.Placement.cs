using Robust.Client.GameObjects;
using System.Numerics;
using Content.Shared._DeepLagoon.Admin;
using Content.Shared.Administration;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.UserInterface;
using Robust.Shared.Graphics;
using Robust.Shared.Input;
using Robust.Shared.Map;

namespace Content.Client._DeepLagoon.Admin;

public sealed partial class AdminMapUploadSystem
{
    [Dependency] private IInputManager _input = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;
    [Dependency] private IEyeManager _eye = default!;
    [Dependency] private IClyde _clyde = default!;
    [Dependency] private IOverlayManager _overlays = default!;

    private AdminMapPlacementReadyEvent? _preview;
    private IClydeViewport? _previewViewport;
    private bool _rotating, _confirming;
    private Vector2 _rotationMouse;
    private float _rotationStart;
    private Box2 _renderBounds;

    public bool HasPreview => _preview != null;
    public bool CanConfirmPlacement => HasPreview && _previewViewport?.Eye != null && !_confirming;
    public Vector2 PlacementPosition { get; private set; }
    public float PlacementRotation { get; private set; }
    public IReadOnlyDictionary<string, int> MissingPrototypes { get; private set; } = new Dictionary<string, int>();

    private void InitializePlacement()
    {
        UpdatesOutsidePrediction = true;
        SubscribeNetworkEvent<AdminMapPlacementReadyEvent>(OnPreview);
        _input.FirstChanceOnKeyEvent += PlacementKey;
        _overlays.AddOverlay(new AdminMapPlacementOverlay(this));
    }

    private void OnPreview(AdminMapPlacementReadyEvent ev)
    {
        if (_request != ev.Request || _preview?.Request == ev.Request)
            return;
        ClearPlacement();
        _preview = ev;
        _uploadBytes = null;
        _inFlight.Clear();
        RefreshLibrary();
        MissingPrototypes = ev.MissingPrototypes;
        _ui.KeyboardFocused?.ReleaseKeyboardFocus();
        PlacementPosition = ev.Position;
        PlacementRotation = 0;
        _expires = _timing.RealTime + TimeSpan.FromSeconds(AdminMapUploadLimits.TimeoutSeconds);
        _completed?.Invoke("dl-map-upload-preview");
    }

    public void ConfirmPlacement()
    {
        if (_preview is not { } preview || !CanConfirmPlacement)
            return;
        _rotating = false;
        _confirming = true;
        RaiseNetworkEvent(new AdminMapPlacementConfirmEvent(preview.Request, PlacementPosition, PlacementRotation));
    }

    public void CancelPlacement()
    {
        if (_request is not { } request)
            return;
        if (_net.IsConnected)
            RaiseNetworkEvent(new AdminMapPlacementCancelEvent(request));
        Finish("dl-map-upload-cancelled");
    }

    private void ClearPlacement()
    {
        _preview = null;
        _previewViewport?.Dispose();
        _previewViewport = null;
        _rotating = _confirming = false;
    }

    private bool OverViewport()
    {
        var control = _ui.MouseGetControl(_input.MouseScreenPosition);
        while (control != null)
        {
            if (ReferenceEquals(control, _eye.MainViewport))
                return true;
            control = control.Parent;
        }
        return false;
    }

    private void PlacementKey(KeyEventArgs args, KeyEventType type)
    {
        if (_preview == null || _confirming || args.Handled)
            return;
        if (args.Key == Keyboard.Key.MouseMiddle)
        {
            if (type == KeyEventType.Up && _rotating)
            {
                _rotating = false;
                args.Handle();
            }
            else if (type == KeyEventType.Down && OverViewport())
            {
                _rotating = true;
                _rotationMouse = _input.MouseScreenPosition.Position;
                _rotationStart = PlacementRotation;
                args.Handle();
            }
            return;
        }
        if (_ui.KeyboardFocused != null)
            return;
        if (args.Key == Keyboard.Key.Escape)
        {
            args.Handle();
            if (type == KeyEventType.Down)
                CancelPlacement();
            return;
        }
        if (!args.Control)
            return;
        var step = args.Key switch
        {
            Keyboard.Key.Left => new Vector2(-1, 0),
            Keyboard.Key.Right => new Vector2(1, 0),
            Keyboard.Key.Up => new Vector2(0, 1),
            Keyboard.Key.Down => new Vector2(0, -1),
            _ => Vector2.Zero
        };
        if (step == Vector2.Zero)
            return;
        args.Handle();
        if (type != KeyEventType.Up)
            PlacementPosition += (-_eye.CurrentEye.Rotation).RotateVec(step * (args.Shift ? 10 : 1));
    }

    private void UpdatePlacement()
    {
        if (_preview is not { } preview)
            return;
        if (!CanUpload || _players.LocalEntity is not { } ghost ||
            !TryGetEntity(preview.DestinationMap, out var destination) || Transform(ghost).MapUid != destination)
        {
            CancelPlacement();
            return;
        }
        if (!_clyde.IsFocused)
            _rotating = false;
        if (_rotating)
        {
            // Horizontal MMB drag rotates continuously; this also works when the pivot is under the cursor.
            PlacementRotation = (float) new Angle(_rotationStart +
                (_input.MouseScreenPosition.Position.X - _rotationMouse.X) * 0.01f).Reduced().Theta;
        }
        if (_previewViewport != null || !TryGetEntity(preview.PreviewMap, out var map) ||
            !TryComp<TransformComponent>(map, out var mapTransform) || mapTransform.MapID == MapId.Nullspace)
            return;

        var bounds = preview.Bounds.Enlarged(1);
        var scale = MathF.Min(1, 2048f / (MathF.Max(bounds.Width, bounds.Height) * EyeManager.PixelsPerMeter));
        var size = new Vector2i(Math.Max(1, (int) MathF.Ceiling(bounds.Width * EyeManager.PixelsPerMeter * scale)),
            Math.Max(1, (int) MathF.Ceiling(bounds.Height * EyeManager.PixelsPerMeter * scale)));
        var extent = (Vector2) size / (EyeManager.PixelsPerMeter * scale);
        _renderBounds = Box2.FromDimensions(-extent / 2, extent);
        _previewViewport = _clyde.CreateViewport(size, "Admin map placement");
        _previewViewport.ClearColor = new Color(0, 0, 0, 0);
        _previewViewport.Eye = new Robust.Shared.Graphics.Eye
        {
            Position = new MapCoordinates(bounds.Center, mapTransform.MapID),
            Scale = new Vector2(scale), DrawFov = false, DrawLight = false
        };
        _previewViewport.AutomaticRender = true;
    }

    internal void DrawPlacement(in OverlayDrawArgs args)
    {
        if (_preview is not { } preview || _previewViewport == null ||
            !TryGetEntity(preview.DestinationMap, out var destination) ||
            !TryComp<TransformComponent>(destination, out var transform) || transform.MapID != args.MapId)
            return;
        var handle = args.WorldHandle;
        handle.SetTransform(Matrix3Helpers.CreateTransform(PlacementPosition, new Angle(PlacementRotation)));
        handle.DrawTextureRect(_previewViewport.RenderTarget.Texture, _renderBounds, Color.White.WithAlpha(0.45f));
        handle.SetTransform(Matrix3x2.Identity);
    }

    internal void DrawMissingPrototypes(in OverlayDrawArgs args)
    {
        if (!_admins.HasFlag(AdminFlags.Mapping))
            return;
        var handle = args.WorldHandle;
        var transformSystem = EntityManager.System<TransformSystem>();
        EntityUid? previewMap = null;
        EntityUid? destinationMap = null;
        if (_preview is { } ready)
        {
            // The ready event can arrive before the staging map's first entity state.
            TryGetEntity(ready.PreviewMap, out previewMap);
            TryGetEntity(ready.DestinationMap, out destinationMap);
        }
        // Preview maps are deliberately paused, but their diagnostics must still draw.
        var query = EntityManager.AllEntityQueryEnumerator<AdminMapMissingPrototypeComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var xform))
        {
            var matrix = transformSystem.GetRenderWorldMatrix(uid);
            if (_preview is { } placement && previewMap != null && xform.MapUid == previewMap &&
                TryComp<TransformComponent>(destinationMap, out var destination) && destination.MapID == args.MapId)
            {
                matrix *= Matrix3x2.CreateTranslation(-placement.Bounds.Center) *
                    Matrix3Helpers.CreateTransform(PlacementPosition, new Angle(PlacementRotation));
            }
            else if (xform.MapID != args.MapId)
                continue;

            // The missing prototype's artwork may not exist in this build. A red
            // silhouette is a diagnostic footprint, never a fabricated real sprite.
            var box = Box2.FromDimensions(new Vector2(-0.4f), new Vector2(0.8f));
            if (!args.WorldAABB.Intersects(matrix.TransformBox(box)))
                continue;
            handle.SetTransform(matrix);
            handle.DrawRect(box, Color.Red.WithAlpha(0.4f));
            handle.DrawRect(box, Color.Red, filled: false);
            handle.DrawLine(new Vector2(-0.3f, -0.3f), new Vector2(0.3f, 0.3f), Color.Red);
            handle.DrawLine(new Vector2(-0.3f, 0.3f), new Vector2(0.3f, -0.3f), Color.Red);
        }
        handle.SetTransform(Matrix3x2.Identity);
    }

    private void ShutdownPlacement()
    {
        _input.FirstChanceOnKeyEvent -= PlacementKey;
        _overlays.RemoveOverlay<AdminMapPlacementOverlay>();
        ClearPlacement();
    }
}
