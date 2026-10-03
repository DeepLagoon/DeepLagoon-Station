using System.Numerics;
using Stopwatch = System.Diagnostics.Stopwatch;
using Content.Shared.CCVar;
using Content.Shared.Tag;
using Content.Shared.Item;
using Content.Shared.Mobs.Components;
using Content.Shared.Doors.Components;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Shared.Configuration;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;
using Robust.Shared.Graphics;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client._DeepLagoon.AmbientOcclusion;

/// <summary>Local contact shading. Runs beneath sprites and the engine's hard FOV pass.</summary>
public sealed class AmbientOcclusionSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IConfigurationManager _cfg = default!;
        [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TagSystem _tags = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SpriteSystem _sprites = default!;
    [Dependency] private readonly IClyde _clyde = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private AmbientOcclusionProfiler? _profiler;
    private ContactOverlay? _overlay;

    internal void StartProfile(Action<string> output, double seconds)
        => _profiler = new AmbientOcclusionProfiler(output, seconds, _timing.RealTime.TotalSeconds);

    internal void StopProfile() => _profiler = null;

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        if (_profiler == null)
            return;
        _profiler.Advance(_timing.RealTime.TotalSeconds, _timing.RealFrameTime.TotalMilliseconds);
        if (_profiler.Finished)
            _profiler = null;
    }

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new ContactOverlay(this);
        _overlays.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        StopProfile();
        _overlays.RemoveOverlay<ContactOverlay>();
        _overlay?.Dispose();
        _overlay = null;
        base.Shutdown();
    }

    internal sealed class ContactOverlay(AmbientOcclusionSystem system) : Overlay
    {
        public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowEntities;

        // Two source pixels per step, with an eight-pixel contact radius on standard tiles.
        private const int Steps = 16;
        private const int Radius = 4;
        private readonly Dictionary<Vector2i, bool> _walls = new();
        private List<Entity<MapGridComponent>> _grids = new();
        private readonly HashSet<EntityUid> _entities = new();
        private readonly List<(EntityUid Uid, EntityUid Grid, Vector2 Position, Vector2 Size, float Alpha)> _objects = new();
        private readonly List<(EntityUid Uid, EntityUid Grid, Vector2 Position, Vector2 Size, float Alpha)> _movingObjects = new();
        private readonly HashSet<EntityUid> _dynamicCasters = new();
        private readonly List<EntityUid> _silhouetteCasters = new();
        private readonly Dictionary<EntityUid, int> _casterLayers = new();
        private OwnedTexture? _mobTexture;
        private readonly List<(EntityUid Uid, Box2 Bounds)> _blockers = new();
        private readonly AmbientOcclusionVisibilityIndex _visibility = new();
        private readonly Dictionary<Vector2i, float[]> _contacts = new();
        private readonly Stack<float[]> _samples = new();
        private const int MaxObjects = 512;
        private const int MaxContactTiles = 512;
        private readonly Dictionary<EntityUid, AmbientOcclusionMask> _masks = new();
        private readonly List<EntityUid> _unusedMasks = new();
        private readonly List<(Vector2i Position, int Walls)> _tileStates = new();
        private AmbientOcclusionFrameStats _stats;
        private bool _measure, _drawEntities, _legacy, _silhouette;

        private long Timestamp() => _measure ? Stopwatch.GetTimestamp() : 0;
        private double Elapsed(long start) => _measure ? (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency : 0;

        protected override void Draw(in OverlayDrawArgs args)
        {
            var mode = system._profiler?.Mode;
            var enabled = mode.HasValue ? mode != AmbientOcclusionProfileMode.Off : system._cfg.GetCVar(CCVars.AmbientOcclusionEnabled);
            if (!enabled || args.MapId == MapId.Nullspace)
                return;

            _measure = system._profiler != null;
            _stats = default;
            _stats.Draws = 1;
            _drawEntities = mode.HasValue ? mode != AmbientOcclusionProfileMode.Walls : system._cfg.GetCVar(CCVars.AmbientOcclusionEntities);
            _legacy = mode == AmbientOcclusionProfileMode.Legacy;
            _silhouette = mode.HasValue ? mode == AmbientOcclusionProfileMode.Silhouette :
                system._cfg.GetCVar(CCVars.AmbientOcclusionSilhouettes);
            var started = Timestamp();
            if (_silhouette)
                DrawSilhouetteScene(args);
            else if (_legacy)
                DrawLegacy(args);
            else
                DrawMasks(args);
            _stats.CpuMs = Elapsed(started);
            system._profiler?.Record(_stats);
        }

        protected override void DisposeBehavior()
        {
            _mobTexture?.Dispose();
            foreach (var mask in _masks.Values)
                mask.Dispose();
            _masks.Clear();
            base.DisposeBehavior();
        }

        private void DrawMasks(in OverlayDrawArgs args)
        {
            var handle = args.WorldHandle;
            handle.UseShader(null);
            var intensity = GetIntensity(system._cfg.GetCVar(CCVars.AmbientOcclusionIntensity));
            _grids.Clear();
            system._map.FindGridsIntersecting(args.MapId, args.WorldAABB, ref _grids);
            var started = Timestamp();
            CollectObjects(args);
            _stats.CollectMs += Elapsed(started);
            // Validate visibility every frame before cache comparison. Camera motion alone need not
            // rerasterize the mask when the exact set of visible contacts remains unchanged.
            started = Timestamp();
            if ((_drawEntities || _silhouette) && args.Viewport.Eye is { DrawFov: true } visibilityEye)
            {
                UpdateVisibilityIndex();
                var write = 0;
                for (var i = 0; i < _objects.Count; i++)
                {
                    var obj = _objects[i];
                    var blocked = _visibility.IsBlocked(visibilityEye.Position.Position, obj.Position, obj.Uid);
                    if (_measure) _stats.LosTests += _visibility.Tests;
                    if (!blocked) _objects[write++] = obj;
                }
                _objects.RemoveRange(write, _objects.Count - write);
            }
            else if (args.Viewport.Eye == null)
                _objects.Clear();
            _stats.ContactMs += Elapsed(started);
            if (_objects.Count > MaxObjects)
                _objects.RemoveRange(MaxObjects, _objects.Count - MaxObjects);
            _movingObjects.Clear();
            _silhouetteCasters.Clear();
            var mobs = system.GetEntityQuery<MobStateComponent>();
            var staticCount = 0;
            for (var i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                if (_silhouette && !mobs.HasComponent(obj.Uid)) _silhouetteCasters.Add(obj.Uid);
                else if (_dynamicCasters.Contains(obj.Uid)) _movingObjects.Add(obj);
                else _objects[staticCount++] = obj;
            }
            _objects.RemoveRange(staticCount, _objects.Count - staticCount);
            foreach (var (uid, grid) in _grids)
            {
                _walls.Clear();
                var inverse = system._transform.GetInvWorldMatrix(uid);
                var local = inverse.TransformBox(args.WorldBounds);
                var min = (local.BottomLeft / grid.TileSize).Floored();
                var max = (local.TopRight / grid.TileSize).Ceiled();
                var width = (max.X - min.X) * Steps;
                var height = (max.Y - min.Y) * Steps;
                // Bound texture memory at extreme spectator zooms. Normal gameplay is far below this limit.
                if (width <= 0 || height <= 0 || width > 1024 || height > 1024)
                    continue;
                if (!_masks.TryGetValue(uid, out var mask))
                    _masks.Add(uid, mask = new AmbientOcclusionMask());
                mask.LastUsedFrame = system._timing.CurFrame;
                started = Timestamp();
                _tileStates.Clear();
                foreach (var tile in system._map.GetTilesIntersecting(uid, grid, args.WorldAABB))
                {
                    var p = tile.GridIndices;
                    if (_measure) _stats.Tiles++;
                    if (p.X < min.X || p.Y < min.Y || p.X >= max.X || p.Y >= max.Y)
                        continue;
                    if (IsWall(uid, grid, p))
                    {
                        _tileStates.Add((p, 16));
                        continue;
                    }
                    var walls = _silhouette ? 0 : (IsWall(uid, grid, p + new Vector2i(-1, 0)) ? 1 : 0) |
                                (IsWall(uid, grid, p + new Vector2i(1, 0)) ? 2 : 0) |
                                (IsWall(uid, grid, p + new Vector2i(0, -1)) ? 4 : 0) |
                                (IsWall(uid, grid, p + new Vector2i(0, 1)) ? 8 : 0);
                    _tileStates.Add((p, walls));
                }
                var key = new AmbientOcclusionSceneKey(min, max, _drawEntities ? inverse : Matrix3x2.Identity,
                    Vector2.Zero, false, intensity, _drawEntities);
                var cached = mask.Scene.Matches(key, _objects, _blockers, _tileStates);
                if (_measure && !cached)
                {
                    switch (mask.Scene.LastMiss)
                    {
                        case 1: _stats.MissKey++; break;
                        case 2: _stats.MissObjects++; break;
                        case 3: _stats.MissBlockers++; break;
                        case 4: _stats.MissTiles++; break;
                    }
                }
                _stats.ComposeMs += Elapsed(started);
                if (cached)
                {
                    if (_measure) _stats.CacheHits++;
                }
                else
                {
                    if (_measure) _stats.Rebuilds++;
                    mask.Prepare(system._clyde, width, height);
                    started = Timestamp();
                    BuildContacts(uid, grid, inverse, args);
                    _stats.ContactMs += Elapsed(started);
                    started = Timestamp();
                    foreach (var (p, walls) in _tileStates)
                    {
                        if (walls == 16) continue;
                        _contacts.TryGetValue(p, out var contacts);
                        if (walls == 0 && contacts == null) continue;
                        AmbientOcclusionMask.ComposeTile(mask.Pixels, mask.Capacity.X, height,
                            (p.X - min.X) * Steps, (p.Y - min.Y) * Steps, walls, contacts, intensity);
                    }
                    _stats.ComposeMs += Elapsed(started);
                    mask.StoreBase();
                    mask.Scene.Store(key, _objects, _blockers, _tileStates);
                }
                // Restore the static raster, then max-compose only tiles touched by dynamic contacts.
                // One final texture preserves the original non-stacking darkness budget.
                started = Timestamp();
                mask.RestoreBase();
                _stats.ComposeMs += Elapsed(started);
                started = Timestamp();
                BuildContacts(uid, grid, inverse, args, moving: true);
                _stats.ContactMs += Elapsed(started);
                started = Timestamp();
                foreach (var (p, contacts) in _contacts)
                {
                    if (p.X < min.X || p.Y < min.Y || p.X >= max.X || p.Y >= max.Y) continue;
                    AmbientOcclusionMask.ComposeTile(mask.Pixels, mask.Capacity.X, height,
                        (p.X - min.X) * Steps, (p.Y - min.Y) * Steps, 0, contacts, intensity, preserve: true);
                }
                _stats.ComposeMs += Elapsed(started);
                started = Timestamp();
                if (mask.Upload() && _measure) _stats.UploadBytes += mask.UploadedBytes;
                _stats.UploadMs += Elapsed(started);
                started = Timestamp();
                handle.SetTransform(system._transform.GetWorldMatrix(system.Transform(uid)));
                handle.DrawTextureRectRegion(mask.Texture!, new Box2(min.X * grid.TileSize, min.Y * grid.TileSize,
                    max.X * grid.TileSize, max.Y * grid.TileSize), subRegion: new UIBox2(0, 0, width, height));
                if (_measure) _stats.Commands++;
                _stats.SubmitMs += Elapsed(started);
                ClearContacts();
            }
            // Release removed/unused grids, and bound retained buffers when visiting many grids.
            started = Timestamp();
            if (_silhouette) DrawSilhouettes(args, intensity);
            _stats.SubmitMs += Elapsed(started);
            _unusedMasks.Clear();
            foreach (var uid in _masks.Keys)
                if (!system.HasComp<MapGridComponent>(uid) || system._timing.CurFrame - _masks[uid].LastUsedFrame > 600 ||
                    (_masks.Count > 8 && _masks[uid].LastUsedFrame != system._timing.CurFrame))
                    _unusedMasks.Add(uid);
            foreach (var uid in _unusedMasks)
            {
                _masks[uid].Dispose();
                _masks.Remove(uid);
            }
            _walls.Clear();
            _grids.Clear();
            handle.SetTransform(Matrix3x2.Identity);
        }

        private void UpdateVisibilityIndex()
        {
            var rebuilt = _visibility.EnsureCurrent(_blockers);
            if (!_measure) return;
            if (rebuilt) _stats.IndexRebuilds++;
            else _stats.IndexHits++;
        }

        private void DrawSilhouetteScene(in OverlayDrawArgs args)
        {
            // Switching from comparison modes must also release their large retained textures.
            foreach (var mask in _masks.Values) mask.Dispose();
            _masks.Clear();
            args.WorldHandle.UseShader(null);
            var started = Timestamp();
            CollectObjects(args);
            _stats.CollectMs += Elapsed(started);
            _silhouetteCasters.Clear();
            _movingObjects.Clear();
            var eye = args.Viewport.Eye;
            if (eye == null) return;
            started = Timestamp();
            if (eye.DrawFov) UpdateVisibilityIndex();
            var mobs = system.GetEntityQuery<MobStateComponent>();
            var count = 0;
            foreach (var obj in _objects)
            {
                if (eye.DrawFov)
                {
                    var blocked = _visibility.IsBlocked(eye.Position.Position, obj.Position, obj.Uid);
                    if (_measure) _stats.LosTests += _visibility.Tests;
                    if (blocked) continue;
                }
                if (count++ >= MaxObjects) break;
                if (mobs.HasComponent(obj.Uid)) _movingObjects.Add(obj);
                else _silhouetteCasters.Add(obj.Uid);
            }
            _stats.ContactMs += Elapsed(started);
            var intensity = GetIntensity(system._cfg.GetCVar(CCVars.AmbientOcclusionIntensity));
            started = Timestamp();
            DrawMobSpots(args, intensity);
            DrawSilhouettes(args, intensity);
            _stats.SubmitMs += Elapsed(started);
            _walls.Clear();
        }

        private void DrawMobSpots(in OverlayDrawArgs args, float intensity)
        {
            if (_movingObjects.Count == 0) return;
            if (_mobTexture == null)
            {
                _mobTexture = system._clyde.CreateBlankTexture<Rgba32>(new Vector2i(Steps, Steps), "ao-mob-spot",
                    new TextureLoadParameters { SampleParameters = new TextureSampleParameters { Filter = false } });
                _mobTexture.SetSubImage<Rgba32>(Vector2i.Zero, new Vector2i(Steps, Steps), AmbientOcclusionMobSpot.MakePixels());
                if (_measure) _stats.UploadBytes += Steps * Steps * 4;
            }
            var grids = system.GetEntityQuery<MapGridComponent>();
            foreach (var obj in _movingObjects)
            {
                if (!grids.TryGetComponent(obj.Grid, out var grid)) continue;
                _walls.Clear();
                var centre = Vector2.Transform(obj.Position, system._transform.GetInvWorldMatrix(obj.Grid));
                var radius = obj.Size / 2 + new Vector2(grid.TileSize * 0.125f);
                var footprint = new Box2(centre - radius, centre + radius);
                var min = (footprint.BottomLeft / grid.TileSize).Floored();
                var max = (footprint.TopRight / grid.TileSize).Floored();
                args.WorldHandle.SetTransform(system._transform.GetWorldMatrix(system.Transform(obj.Grid)));
                for (var y = min.Y; y <= max.Y; y++)
                for (var x = min.X; x <= max.X; x++)
                {
                    var tile = new Vector2i(x, y);
                    if (system._map.GetTileRef(obj.Grid, grid, tile).Tile.IsEmpty || IsWall(obj.Grid, grid, tile)) continue;
                    var floor = Box2.FromDimensions(new Vector2(x, y) * grid.TileSize, new Vector2(grid.TileSize));
                    if (!AmbientOcclusionMobSpot.TryClip(footprint, floor, out var quad, out var source)) continue;
                    args.WorldHandle.DrawTextureRectRegion(_mobTexture, quad,
                        new Color(0f, 0f, 0f, obj.Alpha * intensity), source);
                    if (_measure) _stats.Commands++;
                }
            }
        }

        private void DrawSilhouettes(in OverlayDrawArgs args, float intensity)
        {
            var sprites = system.GetEntityQuery<SpriteComponent>();
            var transforms = system.GetEntityQuery<TransformComponent>();
            var eyeRotation = args.Viewport.Eye?.Rotation ?? Angle.Zero;
            var scale = Math.Clamp(system._cfg.GetCVar(CCVars.AmbientOcclusionSilhouetteScale), 100, 140) / 100f;
            var rotation = -eyeRotation;
            // Four source pixels in total, rather than four pixels on each axis.
            var offset = rotation.RotateVec(new Vector2(2.4f, -3.2f) / 32);
            foreach (var uid in _silhouetteCasters)
            {
                if (!sprites.TryGetComponent(uid, out var sprite) || !transforms.TryGetComponent(uid, out var xform)) continue;
                var layers = _casterLayers.GetValueOrDefault(uid);
                if (layers == 0) continue;
                // Bound overlapping layers within a sprite. One stronger copy replaces the soft-edge copies.
                var worldRotation = system._transform.GetWorldRotation(xform);
                var position = system._transform.GetWorldPosition(xform) + offset;
                var direction = sprite.EnableDirectionOverride ? sprite.DirectionOverride : (Direction?) null;
                var core = new Color(0f, 0f, 0f, 0.16f * intensity / layers);
                AmbientOcclusionSilhouette.Render(system._sprites, sprite, args.WorldHandle, eyeRotation,
                    worldRotation, position, direction, core, scale);
                if (_measure) _stats.Commands += layers;
            }
            args.WorldHandle.UseShader(null);
            args.WorldHandle.SetTransform(Matrix3x2.Identity);
        }

        private void DrawLegacy(in OverlayDrawArgs args)
        {

            var intensity = GetIntensity(system._cfg.GetCVar(CCVars.AmbientOcclusionIntensity));

            var handle = args.WorldHandle;
            handle.UseShader(null);
            _grids.Clear();
            system._map.FindGridsIntersecting(args.MapId, args.WorldAABB, ref _grids);
            var started = Timestamp();
            CollectObjects(args);
            _stats.CollectMs += Elapsed(started);
            foreach (var (uid, grid) in _grids)
            {
                _walls.Clear(); // No stale geometry after construction, deletion, PVS changes or grid movement.
                var xform = system.Transform(uid);
                handle.SetTransform(system._transform.GetWorldMatrix(xform));
                started = Timestamp();
                BuildContacts(uid, grid, system._transform.GetInvWorldMatrix(uid), args);
                _stats.ContactMs += Elapsed(started);
                started = Timestamp();
                foreach (var tile in system._map.GetTilesIntersecting(uid, grid, args.WorldAABB))
                {
                    var p = tile.GridIndices;
                    if (_measure) _stats.Tiles++;
                    if (IsWall(uid, grid, p))
                        continue;

                    var west = IsWall(uid, grid, p + new Vector2i(-1, 0));
                    var east = IsWall(uid, grid, p + new Vector2i(1, 0));
                    var south = IsWall(uid, grid, p + new Vector2i(0, -1));
                    var north = IsWall(uid, grid, p + new Vector2i(0, 1));
                    _contacts.TryGetValue(p, out var contact);
                    if (!west && !east && !south && !north && contact == null)
                        continue;

                    var commands = DrawTile(handle, p, grid.TileSize, west, east, south, north, intensity, contact);
                    if (_measure) _stats.Commands += commands;
                }
                _stats.ComposeMs += Elapsed(started); // Legacy composition includes command submission.
                ClearContacts();
            }

            _walls.Clear();
            _grids.Clear();
            handle.SetTransform(Matrix3x2.Identity);
        }

        internal static float GetIntensity(int percent) => Math.Clamp(percent, 100, 300) / 100f;

        private void CollectObjects(in OverlayDrawArgs args)
        {
            _entities.Clear();
            _objects.Clear();
            _dynamicCasters.Clear();
            _casterLayers.Clear();
            _blockers.Clear();
            if (!_drawEntities && !_silhouette)
                return;
            // Explicit spatial lookup, including items without collidable fixtures; no global sprite scan.
            var phaseStarted = Timestamp();
            system._lookup.GetEntitiesIntersecting(args.MapId, args.WorldAABB.Enlarged(0.25f),
                _entities, LookupFlags.Uncontained | LookupFlags.Approximate);
            _stats.LookupMs += Elapsed(phaseStarted);
            if (_measure) _stats.Candidates = _entities.Count;
            phaseStarted = Timestamp();
            var contactRotation = -(args.Viewport.Eye?.Rotation ?? Angle.Zero);
            var metas = system.GetEntityQuery<MetaDataComponent>();
            var transforms = system.GetEntityQuery<TransformComponent>();
            var sprites = system.GetEntityQuery<SpriteComponent>();
            var occluders = system.GetEntityQuery<OccluderComponent>();
            var items = system.GetEntityQuery<ItemComponent>();
            var mobs = system.GetEntityQuery<MobStateComponent>();
            var lights = system.GetEntityQuery<PointLightComponent>();
            var airlocks = system.GetEntityQuery<AirlockComponent>();
            foreach (var uid in _entities)
            {
                // Reject irrelevant candidates before metadata, transforms, tags or layer walks.
                var hasOccluder = occluders.TryGetComponent(uid, out var occluder) && occluder.Enabled;
                var caster = sprites.TryGetComponent(uid, out var sprite) &&
                    CanCastContact(sprite.Visible, sprite.ContainerOccluded, sprite.Color.A, sprite.GetScreenTexture,
                        sprite.DrawDepth) && !items.HasComponent(uid) && !lights.HasComponent(uid) &&
                    (!_silhouette || !airlocks.HasComponent(uid));
                if (!caster && !hasOccluder) continue;
                if (!metas.TryGetComponent(uid, out var meta) ||
                    (meta.Flags & (MetaDataFlags.Detached | MetaDataFlags.InContainer)) != 0 ||
                    meta.EntityLifeStage >= EntityLifeStage.Terminating)
                    continue;
                if (!transforms.TryGetComponent(uid, out var xform)) continue;
                if (_measure) _stats.ProcessedCandidates++;
                if (hasOccluder)
                    _blockers.Add((uid, system._transform.GetWorldMatrix(xform).TransformBox(occluder!.LocalBounds)));
                if (!caster || xform.GridUid is not { } grid) continue;
                // Full entity silhouettes do not need wall-tag checks; wall-only and mask modes do.
                if (!_silhouette || !_drawEntities)
                {
                    var wall = system._tags.HasTag(uid, "Wall");
                    if ((wall && !_silhouette) || (!wall && !_drawEntities)) continue;
                }
                var mob = mobs.HasComponent(uid);
                var layers = 0;
                foreach (var layer in sprite!.AllLayers)
                {
                    if (!layer.Visible || layer.Color.A <= 0) continue;
                    if (_silhouette && !mob && (layer is not SpriteComponent.Layer actual || actual.Blank ||
                        actual.ShaderPrototype == SpriteSystem.UnshadedId || actual.Shader != null ||
                        actual.CopyToShaderParameters != null)) continue;
                    layers++;
                    if (!_silhouette || mob) break;
                }
                if (layers == 0) continue;
                if (_silhouette && !mob) _casterLayers[uid] = layers;

                // Mob animation must not change the footprint or require sprite bounds calculation.
                var bounds = mob || _silhouette ? new Box2(-0.3f, -0.35f, 0.3f, 0.35f) :
                    system._sprites.GetLocalBounds((uid, sprite));
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    continue;

                var size = mob ? new Vector2(0.35f, 0.15f) :
                    new Vector2(Math.Clamp(bounds.Width * 0.9f, 0.2f, 2f),
                        Math.Clamp(bounds.Height * 0.9f, 0.15f, 2f));
                // Billboard contact follows the sprite offset; mobs contact the floor near their feet.
                var position = system._transform.GetWorldPosition(xform);
                var contactOffset = sprite.Offset +
                    (mob ? new Vector2(0, bounds.Bottom + 0.18f) : Vector2.Zero);
                if (contactOffset != Vector2.Zero)
                    position += contactRotation.RotateVec(contactOffset);
                _objects.Add((uid, grid, position, size, sprite.Color.A * (mob ? 0.3f : 1f)));
                if (mob || !xform.Anchored) _dynamicCasters.Add(uid);
            }

            _stats.ProcessMs += Elapsed(phaseStarted);
            phaseStarted = Timestamp();
            var eyePosition = args.Viewport.Eye?.Position.Position ?? args.WorldAABB.Center;
            if (_legacy || _objects.Count > MaxObjects)
                _objects.Sort((a, b) => Vector2.DistanceSquared(a.Position, eyePosition)
                    .CompareTo(Vector2.DistanceSquared(b.Position, eyePosition)));
            _stats.SortMs += Elapsed(phaseStarted);
            if (_measure)
            {
                _stats.Objects = Math.Min(_objects.Count, MaxObjects);
                _stats.Blockers = _blockers.Count;
            }
        }

        internal static bool CanCastContact(bool visible, bool contained, float alpha, bool screenShader, int depth)
            => visible && !contained && alpha > 0 && !screenShader &&
               depth >= (int) Content.Shared.DrawDepth.DrawDepth.FloorObjects &&
               depth <= (int) Content.Shared.DrawDepth.DrawDepth.Overdoors;

        private void ClearContacts()
        {
            foreach (var sample in _contacts.Values)
                _samples.Push(sample);
            _contacts.Clear();
        }

        private void BuildContacts(EntityUid gridUid, MapGridComponent grid, Matrix3x2 inverse, in OverlayDrawArgs args,
            bool moving = false)
        {
            ClearContacts();
            var eye = args.Viewport.Eye;
            if (eye == null)
                return;

            var objects = moving ? _movingObjects : _objects;
            var count = Math.Min(objects.Count, moving ? Math.Max(0, MaxObjects - Math.Min(_objects.Count, MaxObjects)) : MaxObjects);
            for (var i = 0; i < count; i++)
            {
                var obj = objects[i];
                if (obj.Grid != gridUid)
                    continue;

                // FOV is applied after this overlay. Also reject hidden casters so their footprint cannot
                // extend across a wall into a visible floor tile. Occluder bounds are conservative on rotated grids.
                var blocked = false;
                if (eye.DrawFov && _legacy)
                    foreach (var blocker in _blockers)
                    {
                        if (_measure) _stats.LosTests++;
                        if (blocker.Uid != obj.Uid && SegmentIntersects(eye.Position.Position, obj.Position, blocker.Bounds))
                        {
                            blocked = true;
                            break;
                        }
                    }
                if (blocked)
                    continue;

                var centre = Vector2.Transform(obj.Position, inverse) / grid.TileSize;
                var radius = obj.Size / grid.TileSize / 2 + new Vector2(0.125f);
                var min = (centre - radius).Floored();
                var max = (centre + radius).Floored();
                for (var y = min.Y; y <= max.Y; y++)
                for (var x = min.X; x <= max.X; x++)
                {
                    var tile = new Vector2i(x, y);
                    if (system._map.GetTileRef(gridUid, grid, tile).Tile.IsEmpty || IsWall(gridUid, grid, tile))
                        continue;
                    if (!_contacts.TryGetValue(tile, out var values))
                    {
                        if (_contacts.Count >= MaxContactTiles)
                            continue;
                        values = _samples.Count > 0 ? _samples.Pop() : new float[Steps * Steps];
                        Array.Clear(values);
                        _contacts.Add(tile, values);
                    }
                    var samples = _legacy ? AddContactLegacy(values, tile, centre, radius, obj.Alpha) :
                        AddContact(values, tile, centre, radius, obj.Alpha);
                    if (_measure) _stats.ContactSamples += samples;
                }
            }
        }

        internal static int AddContactLegacy(float[] values, Vector2i tile, Vector2 centre, Vector2 radius, float opacity)
        {
            for (var y = 0; y < Steps; y++)
            for (var x = 0; x < Steps; x++)
            {
                var point = new Vector2(tile.X + (x + 0.5f) / Steps, tile.Y + (y + 0.5f) / Steps);
                var delta = (point - centre) / radius;
                var falloff = Math.Max(0, 1 - delta.LengthSquared());
                // Quantized pixel shading and max-compositing prevent darkening from item stacks.
                var alpha = MathF.Round(falloff * falloff * 8) / 8 * 0.22f * Math.Clamp(opacity, 0, 1);
                var index = y * Steps + x;
                values[index] = Math.Max(values[index], alpha);
            }
            return Steps * Steps;
        }

        internal static int AddContact(float[] values, Vector2i tile, Vector2 centre, Vector2 radius, float opacity)
        {
            if (radius.X <= 0 || radius.Y <= 0 || opacity <= 0)
                return 0;
            var min = (centre - radius - new Vector2(tile.X, tile.Y)) * Steps - new Vector2(0.5f);
            var max = (centre + radius - new Vector2(tile.X, tile.Y)) * Steps - new Vector2(0.5f);
            var left = Math.Clamp((int) MathF.Ceiling(min.X), 0, Steps);
            var right = Math.Clamp((int) MathF.Floor(max.X) + 1, 0, Steps);
            var bottom = Math.Clamp((int) MathF.Ceiling(min.Y), 0, Steps);
            var top = Math.Clamp((int) MathF.Floor(max.Y) + 1, 0, Steps);
            var alphaScale = 0.22f * Math.Clamp(opacity, 0, 1);
            for (var y = bottom; y < top; y++)
            {
                var dy = (tile.Y + (y + 0.5f) / Steps - centre.Y) / radius.Y;
                for (var x = left; x < right; x++)
                {
                    var dx = (tile.X + (x + 0.5f) / Steps - centre.X) / radius.X;
                    var falloff = Math.Max(0, 1 - (dx * dx + dy * dy));
                    var alpha = MathF.Round(falloff * falloff * 8) / 8 * alphaScale;
                    var index = y * Steps + x;
                    values[index] = Math.Max(values[index], alpha);
                }
            }
            return (right - left) * (top - bottom);
        }

        internal static bool SegmentIntersects(Vector2 start, Vector2 end, Box2 box)
        {
            var direction = end - start;
            var near = 0f;
            var far = 1f;
            return Clip(start.X, direction.X, box.Left, box.Right, ref near, ref far) &&
                   Clip(start.Y, direction.Y, box.Bottom, box.Top, ref near, ref far);

            static bool Clip(float origin, float delta, float min, float max, ref float near, ref float far)
            {
                if (Math.Abs(delta) < 0.00001f)
                    return origin >= min && origin <= max;
                var a = (min - origin) / delta;
                var b = (max - origin) / delta;
                near = Math.Max(near, Math.Min(a, b));
                far = Math.Min(far, Math.Max(a, b));
                return near <= far;
            }
        }

        private bool IsWall(EntityUid uid, MapGridComponent grid, Vector2i p)
        {
            if (_walls.TryGetValue(p, out var wall))
                return wall;

            var entities = system._map.GetAnchoredEntitiesEnumerator(uid, grid, p);
            while (entities.MoveNext(out var entity))
            {
                if (!system.TryComp<MetaDataComponent>(entity, out var meta) ||
                    (meta.Flags & (MetaDataFlags.Detached | MetaDataFlags.InContainer)) != 0 ||
                    meta.EntityLifeStage >= EntityLifeStage.Terminating ||
                    !system.TryComp<OccluderComponent>(entity, out var occluder) || !occluder.Enabled ||
                    !system._tags.HasTag(entity.Value, "Wall"))
                    continue;

                wall = true;
                break;
            }

            _walls[p] = wall;
            return wall;
        }

        private static float Falloff(int distance)
        {
            var value = Math.Max(0, (Radius - distance - 0.5f) / Radius);
            return value * value;
        }

        internal static int DrawTile(DrawingHandleWorld handle, Vector2i tile, float size,
            bool west, bool east, bool south, bool north, float intensity, float[]? contact = null)
        {
            var step = size / Steps;
            var origin = new Vector2(tile.X * size, tile.Y * size);
            var commands = 0;
            for (var y = 0; y < Steps;)
            {
                var vertical = Vertical(y);
                var top = y + 1;
                while (top < Steps && RowsEqual(y, top))
                    top++;
                for (var x = 0; x < Steps;)
                {
                    var alpha = Alpha(x, y, vertical);
                    var end = x + 1;
                    // Merge equal adjacent samples into strips; corners never accumulate multiple blends.
                    while (end < Steps && Alpha(end, y, vertical) == alpha)
                        end++;
                    if (alpha > 0)
                    {
                        handle.DrawRect(new Box2(origin.X + x * step, origin.Y + y * step,
                            origin.X + end * step, origin.Y + top * step), Color.Black.WithAlpha(alpha));
                        commands++;
                    }
                    x = end;
                }
                y = top;
            }
            return commands;

            float Vertical(int y) => Math.Max(south ? Falloff(y) : 0, north ? Falloff(Steps - 1 - y) : 0);

            bool RowsEqual(int a, int b)
            {
                if (contact == null)
                    return Vertical(a) == Vertical(b);
                for (var x = 0; x < Steps; x++)
                    if (Alpha(x, a, Vertical(a)) != Alpha(x, b, Vertical(b)))
                        return false;
                return true;
            }

            float Alpha(int x, int y, float vertical)
            {
                var horizontal = Math.Max(west ? Falloff(x) : 0, east ? Falloff(Steps - 1 - x) : 0);
                // Combine walls and entities once. New maximum is three times the previous maximum.
                var wall = Math.Min(0.22f, 0.22f * (Math.Max(horizontal, vertical) +
                    0.35f * Math.Min(horizontal, vertical)));
                return Math.Max(wall, contact?[y * Steps + x] ?? 0) * intensity;
            }
        }
    }
}
