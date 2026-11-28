using Content.Shared._DeepLagoon.Voyages;
using System.Linq;
using System.Numerics;
using Content.Client._DeepLagoon.Economy;
using Content.Client._DeepLagoon.SpaceEnvironment;
using Content.Client._FarHorizons.StarSystem;
using Content.Client.UserInterface.Controls;
using Content.Shared._DeepLagoon.SpaceEnvironment;
using Content.Shared._FarHorizons.StarSystem.Helpers;
using Content.Shared._FarHorizons.StarSystem.Prototypes;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;
using Robust.Shared.Graphics;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Maths;

namespace Content.Client._DeepLagoon.Voyages;

public sealed class VoyageMapControl : MapGridControl
{
    private readonly VoyagePrototype _model;
    private OwnedTexture? _fogTexture;
    private readonly HashSet<string> _fogVisible = new();
    private int _fogRevision = -1, _fogSeed;
    private bool _fogShowUnknown;
    private Box2 _fogWorld;
    private readonly PanelContainer _playerBadge;
    private readonly Label _playerLabel;
    private readonly List<Box2> _captionBoxes = new();
    private readonly Font _font;
    private readonly ShaderInstance _surface;
    private readonly Dictionary<string, ShaderInstance?> _portraits = new();
    private readonly Dictionary<string, BoxContainer> _badges = new();
    private readonly Dictionary<string, BoxContainer> _captions = new();
    private readonly Dictionary<string, Label> _labels = new();
    private readonly Dictionary<string, PanelContainer> _researchBadges = new();
    private readonly Dictionary<string, string> _demandKeys = new();
    private HashSet<string> _visibleSystems = new();
    private int _visibilityRevision = -1;
    private string _visibilitySector = "";
    public Vector2? VesselPosition;
    public bool OverviewDots = true;
    public string Selected = VoyagePrototype.Home;
    public string[] Planned = [];
    public bool ShowDemand = true, ShowUnknown = true;
    public event Action<string>? SelectedSystem;
    public event Action? SelectionCleared;
    public event Action<string>? LandingRequested;
    public bool LandingEnabled;
    public string LandingBlockReason = "";
    private readonly Button _planetLanding;
    private static readonly Color PlayerColor = Color.FromHex("#90F3A6");
    private static readonly Color Cyan = Color.FromHex("#63DED6"), Muted = Color.FromHex("#75899D");

    public VoyageMapControl(VoyagePrototype model) : base(170, 3400, 1050)
    {
        _model = model; FitViewport = true; Draggable = true;
        SetSize = new Vector2(float.NaN); MinSize = new Vector2(320, 360);
        HorizontalExpand = VerticalExpand = true;
        _font = new VectorFont(IoCManager.Resolve<IResourceCache>().GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Regular.ttf"), 12);
        _surface = PrototypeManager.Index<ShaderPrototype>("ShuttleRadarSurface").InstanceUnique();
        foreach (var system in model.Systems)
        {
            _portraits[system.Id] = null;
            var caption = new BoxContainer { Orientation = BoxContainer.LayoutOrientation.Vertical,
                SeparationOverride = 8, SetWidth = 230, MouseFilter = MouseFilterMode.Ignore };
            var label = new Label { Name = "SystemLabel-" + system.Id, HorizontalExpand = true,
                Align = Label.AlignMode.Center, ClipText = true, MinHeight = 24, MouseFilter = MouseFilterMode.Ignore };
            var badges = new BoxContainer { Name = "Demand-" + system.Id, Orientation = BoxContainer.LayoutOrientation.Horizontal,
                SeparationOverride = 4, HorizontalAlignment = HAlignment.Center, MouseFilter = MouseFilterMode.Ignore };
            var researched = new PanelContainer { Name = "PlanetResearched-" + system.Id, Visible = false,
                HorizontalAlignment = HAlignment.Center, MouseFilter = MouseFilterMode.Ignore,
                PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#173A2B") },
                Children = { new Label { Text = "[Исследовано]", FontColorOverride = PlayerColor,
                    Margin = new Thickness(6, 2), MouseFilter = MouseFilterMode.Ignore } } };
            caption.AddChild(label); caption.AddChild(researched); caption.AddChild(badges); AddChild(caption);
            _researchBadges[system.Id] = researched;
            _captions[system.Id] = caption; _labels[system.Id] = label; _badges[system.Id] = badges;

        }
        _playerLabel = new Label { Text = "ВЫ ЗДЕСЬ", FontColorOverride = PlayerColor, Align = Label.AlignMode.Center, MouseFilter = MouseFilterMode.Ignore };
        _playerBadge = new PanelContainer { Name = "VoyagePlayerPosition", SetWidth = 156, SetHeight = 28, MouseFilter = MouseFilterMode.Ignore,
            PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#173A2B") }, Children = { _playerLabel } };
        AddChild(_playerBadge);
        _planetLanding = new Button { Name = "PlanetLandingPopup", Text = "Посадка", Visible = false, SetWidth = 125, SetHeight = 30 };
        _planetLanding.OnPressed += _ => LandingRequested?.Invoke(Selected);
        AddChild(_planetLanding);
    }
    public bool CenterOnPlayer()
    {
        var position = OverviewDots ? PlayerWorldPosition() : VesselPosition;
        if (position is not { } point) return false;
        Offset = TargetOffset = point; Recentering = false; return true;
    }
    public void ShowSector() { Offset = TargetOffset = new Vector2(0, 350); WorldRange = ActualRadarRange = 2400; }
    public void SetView(float range, Vector2 center) { Offset = TargetOffset = center; WorldRange = ActualRadarRange = Math.Clamp(range, 170, 3400); }
    private ShaderInstance? Portrait(VoyageDestination system)
    {
        if (_portraits[system.Id] is { } cached) return cached;
            var planet = new Planet(PrototypeManager.Index<PlanetTypePrototype>(system.PlanetType), PrototypeManager, Vector2.Zero);
            PlanetAppearanceGenerator.Apply(planet, (uint)(100 + _model.Systems.IndexOf(system) * 113));
            var star = new Star(PrototypeManager.Index<StarTypePrototype>("TypeGStar"), PrototypeManager)
                { Position = new Vector2(-planet.Radius * 6, planet.Radius * 4) };
            var shader = PlanetShaderFactory.Create(PrototypeManager, planet, star);
            if (shader != null)
            {
                var extent = planet.Radius * MathF.Max(1.3f, (planet.Rings?.RadiusOuter ?? 1) * 1.3f);
                shader.SetParameter("planetaryRadiusFactor", 1f);
                shader.SetParameter("parallaxCenter", Vector2.Zero);
                shader.SetParameter("viewportMin", new Vector2(-extent));
                shader.SetParameter("viewportSize", new Vector2(extent * 2));
            }
            _portraits[system.Id] = shader;
            return shader;
    }
    private int _portraitSeed;
    public Vector2 ScreenPosition(Vector2 world) => (world - Offset) * new Vector2(MinimapScale, -MinimapScale) + MidPointVector;
    private void RefreshVisibility()
    {
        if (_visibilityRevision == _model.Revision && _visibilitySector == _model.PlayerSector) return;
        _visibleSystems = VoyageVisibility.VisibleSystems(_model, _model.PlayerSector);
        _visibilityRevision = _model.Revision; _visibilitySector = _model.PlayerSector;
    }
    public bool IsSystemVisible(string id)
    {
        if (!OverviewDots) return true;
        RefreshVisibility(); return _visibleSystems.Contains(id);
    }
    public string? Hit(Vector2 pixel) => _model.Systems.Where(s => (s.Known || ShowUnknown) && IsSystemVisible(s.Id))
        .Where(s => Vector2.Distance(ScreenPosition(s.Position), pixel) <= 43 * UIScale)
        .OrderBy(s => Vector2.DistanceSquared(ScreenPosition(s.Position), pixel)).FirstOrDefault()?.Id;

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        if (args.Function == EngineKeyFunctions.UIClick && Hit(args.RelativePixelPosition) is { } id)
        { Selected = id; SelectedSystem?.Invoke(id); args.Handle(); return; }
        if (args.Function == EngineKeyFunctions.UIClick && !OverviewDots)
        { Selected = ""; _planetLanding.Visible = false; SelectionCleared?.Invoke(); }
        base.KeyBindDown(args);
    }
    protected override void MouseMove(GUIMouseMoveEventArgs args)
    {
        base.MouseMove(args);
        var system = Hit(args.RelativePixelPosition);
        ToolTip = system == null ? null : _model.System(system).Name;
    }
    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        var selectedPlanet = !OverviewDots ? _model.Systems.FirstOrDefault(s => s.Id == Selected) : null;
        _planetLanding.Visible = selectedPlanet != null;
        _planetLanding.Disabled = !LandingEnabled;
        _planetLanding.ToolTip = !LandingEnabled && LandingBlockReason.Length > 0 ? LandingBlockReason : null;
        if (selectedPlanet != null)
        {
            var point = ScreenPosition(selectedPlanet.Position) / UIScale;
            _planetLanding.Visible = point.X >= 0 && point.Y >= 0 && point.X <= Width && point.Y <= Height;
            LayoutContainer.SetPosition(_planetLanding, new Vector2(Math.Clamp(point.X - 62, 0, Math.Max(0, Width - 125)), Math.Clamp(point.Y - 68, 0, Math.Max(0, Height - 30))));
        }
        if (_portraitSeed != _model.Seed)
        {
            foreach (var shader in _portraits.Values) shader?.Dispose();
            foreach (var id in _portraits.Keys.ToArray()) _portraits[id] = null;
            _portraitSeed = _model.Seed; _fogTexture?.Dispose(); _fogTexture = null;
        }
        Box2? playerBox = null;
        var playerPosition = PlayerWorldPosition();
        _playerBadge.Visible = OverviewDots && playerPosition != null;
        if (playerPosition is { } where)
        {
            var point = ScreenPosition(where) / UIScale;
            _playerLabel.Text = _model.PlayerFlight == Guid.Empty ? "ВЫ ЗДЕСЬ" : "ВЫ В ПУТИ";
            var badgePosition = new Vector2(Math.Clamp(point.X - 78, 2, Math.Max(2, Width - 158)), Math.Clamp(point.Y - 70, 2, Math.Max(2, Height - 30)));
            LayoutContainer.SetPosition(_playerBadge, badgePosition); playerBox = Box2.FromDimensions(badgePosition, new Vector2(156, 28));
        }
        _captionBoxes.Clear(); if (_playerBadge.Visible && playerBox is { } playerBounds) _captionBoxes.Add(playerBounds);
        foreach (var system in _model.Systems.OrderBy(s => s.Id == _model.PlayerSector ? 0 : s.Id == Selected ? 1 : 2))
        {
            var position = ScreenPosition(system.Position) / UIScale;
            var caption = _captions[system.Id]; var row = _badges[system.Id];
            var focused = system.Id == Selected || system.Id == _model.PlayerSector;
            caption.Visible = IsSystemVisible(system.Id) && (system.Known && (!OverviewDots || MinimapScale / UIScale > .45f) || focused) && (system.Known || ShowUnknown) && position.X > -60 && position.Y > -60 && position.X < Width + 60 && position.Y < Height - 30;
            _researchBadges[system.Id].Visible = system.Researched;
            _labels[system.Id].Text = system.Known ? system.Name : "Сигнал " + system.Name;
            _labels[system.Id].FontColorOverride = system.Id == _model.PlayerSector ? PlayerColor : system.Known ? Color.FromHex("#DAEBEC") : Muted;
            row.Visible = ShowDemand && system.Known && (system.Orders.Length > 0 || system.Goods?.Length > 0);
            var prototypes = system.Orders.Select(o => o.Prototype).Concat(system.Goods ?? []).Distinct().Take(3).ToArray();
            var key = string.Join(",", prototypes);
            if (!_demandKeys.TryGetValue(system.Id, out var previous) || previous != key)
            {
                _demandKeys[system.Id] = key; row.DisposeAllChildren();
                foreach (var prototype in prototypes)
                {
                    var icon = new TradeItemPreview { SetSize = new Vector2(36), Margin = new Thickness(2), MouseFilter = MouseFilterMode.Ignore };
                    icon.SetPrototype(prototype);
                    var chip = new PanelContainer { SetSize = new Vector2(40), MouseFilter = MouseFilterMode.Ignore,
                        PanelOverride = new StyleBoxFlat { BackgroundColor = Color.FromHex("#122B35") } };
                    chip.AddChild(icon); row.AddChild(chip);
                }
            }
            var radius = Math.Clamp(27 * MinimapScale, 7 * UIScale, 30 * UIScale) / UIScale;
            var x = Math.Clamp(position.X - 115, 0, Math.Max(0, Width - 230));
            var height = (row.Visible ? 76 : 28) + (system.Researched ? 36 : 0);
            var placements = new[] { new Vector2(x, position.Y + radius + 8), new Vector2(x, position.Y - radius - height - 8),
                new Vector2(x, position.Y + radius + height + 16) };
            var placed = false;
            foreach (var candidate in placements)
            {
                var box = Box2.FromDimensions(candidate, new Vector2(230, height));
                if (candidate.Y < 0 || candidate.Y + height > Height || _captionBoxes.Any(other => other.Intersects(box))) continue;
                LayoutContainer.SetPosition(caption, candidate); if (caption.Visible) _captionBoxes.Add(box); placed = true; break;
            }
            if (!placed) caption.Visible = false;
        }
    }
    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        _surface.SetParameter("viewport_size", (Vector2)PixelSize);
        handle.UseShader(_surface); handle.DrawTextureRect(Texture.White, PixelSizeBox); handle.UseShader(null);
        var grid = Color.FromHex("#153039").WithAlpha(.35f);
        var step = 100; while (step * MinimapScale < 60 * UIScale) step *= 2;
        for (var x = -3200; x <= 3200; x += step)
        {
            handle.DrawLine(ScreenPosition(new(x, -2400)), ScreenPosition(new(x, 2800)), grid);
            handle.DrawLine(ScreenPosition(new(-2800, x)), ScreenPosition(new(2800, x)), grid);
        }
        for (var i = 0; i < 70; i++)
        {
            var p = ScreenPosition(new Vector2((i * 137 % 1000) - 500, (i * 211 % 1000) - 500));
            handle.DrawCircle(p, i % 5 == 0 ? 1.3f : .7f, Muted.WithAlpha(.35f));
        }
        if (OverviewDots) DrawFog(handle);
        foreach (var lane in _model.Lanes)
        {
            if (!IsSystemVisible(lane.From) || !IsSystemVisible(lane.To)) continue;
            if (!lane.Charted && (!ShowUnknown || !_model.System(lane.From).Known && !_model.System(lane.To).Known && lane.From != Selected && lane.To != Selected)) continue;
            var start = ScreenPosition(_model.System(lane.From).Position);
            var end = ScreenPosition(_model.System(lane.To).Position);
            var selected = Enumerable.Range(1, Math.Max(0, Planned.Length - 1)).Any(i =>
                Planned[i - 1] == lane.From && Planned[i] == lane.To || Planned[i - 1] == lane.To && Planned[i] == lane.From);
            var piracy = _model.Piracy.GetValueOrDefault(VoyageGalaxy.LaneKey(lane.From, lane.To));
            var colour = selected ? Cyan : !lane.Charted ? Muted.WithAlpha(.45f) : piracy > .6 ? Color.FromHex("#E16E68") : piracy > .2 || lane.Dangerous ? Color.FromHex("#D18B61") : piracy <= .03 ? Color.FromHex("#4C9A72") : Color.FromHex("#34636F");
            if (lane.Charted && piracy > .25)
            {
                var mid = (start + end) / 2;
                handle.DrawCircle(mid, 3 * UIScale, colour.WithAlpha(.7f));
                if (selected) handle.DrawString(_font, mid + new Vector2(7, -10) * UIScale, ((int)(piracy * 100)) + "%", UIScale, colour);
            }
            if (!lane.Charted) Dash(handle, start, end, colour);
            else
            {
                handle.DrawLine(start, end, colour);
                if (selected)
                {
                    var normal = Vector2.Normalize(new Vector2(-(end - start).Y, (end - start).X));
                    handle.DrawLine(start + normal * 2, end + normal * 2, colour.WithAlpha(.2f));
                    handle.DrawLine(start - normal * 2, end - normal * 2, colour.WithAlpha(.2f));
                }
            }
        }
        foreach (var flight in _model.Flights.Where(f => f.Progress(_model.Clock) < 1).OrderBy(f => f.IsLocal))
            DrawFlightRoute(handle, flight);
        foreach (var system in _model.Systems)
        {
            if (!IsSystemVisible(system.Id) || !system.Known && !ShowUnknown) continue;
            var p = ScreenPosition(system.Position);
            if (p.X < -70 || p.Y < -70 || p.X > PixelWidth + 70 || p.Y > PixelHeight + 70) continue;
            var radius = Math.Clamp(27 * MinimapScale, 7 * UIScale, 30 * UIScale);
            if (!system.Known && OverviewDots && system.Id != Selected && system.Id != _model.PlayerSector)
            {
                handle.DrawCircle(p, 2.5f * UIScale, Muted.WithAlpha(.7f)); continue;
            }
            if (system.Id == "centcomm") handle.DrawCircle(p, radius + 6 * UIScale, Color.FromHex("#65D78C").WithAlpha(.55f), false);
            if (OverviewDots && _model.PlayerSector == system.Id)
            {
                handle.DrawCircle(p, radius + 17 * UIScale, PlayerColor.WithAlpha(.16f));
                handle.DrawCircle(p, radius + 17 * UIScale, PlayerColor, false);
                handle.DrawCircle(p, radius + 20 * UIScale, PlayerColor.WithAlpha(.45f), false);
            }
            if (Selected == system.Id)
            {
                handle.DrawCircle(p, radius + 9 * UIScale, Cyan.WithAlpha(.08f));
                handle.DrawCircle(p, radius + 9 * UIScale, Cyan.WithAlpha(.8f), false);
                handle.DrawCircle(p, radius + 13 * UIScale, Cyan.WithAlpha(.18f), false);
            }
            if (system.Known && (!OverviewDots || MinimapScale / UIScale > .25f) && Portrait(system) is { } portrait)
            {
                handle.UseShader(portrait);
                handle.DrawTextureRect(Texture.White, UIBox2.FromDimensions(p - new Vector2(radius), new Vector2(radius * 2)));
                handle.UseShader(null);
            }
            else
            {
                if (OverviewDots && MinimapScale / UIScale <= .25f) { handle.DrawCircle(p, 3 * UIScale, system.Known ? Cyan : Muted); continue; }
                handle.DrawCircle(p, radius, Muted.WithAlpha(.08f));
                handle.DrawCircle(p, radius, Muted.WithAlpha(.6f), false);
                handle.DrawString(_font, p + new Vector2(-4, 5) * UIScale, "?", UIScale, Muted);
            }

        }
        foreach (var flight in _model.Flights.Where(f => f.Progress(_model.Clock) < 1))
        {
            var p = ScreenPosition(_model.Position(flight));
            var direction = Vector2.Normalize(_model.Direction(flight) * new Vector2(1, -1));
            var normal = new Vector2(-direction.Y, direction.X);
            var colour = FlightColor(flight);
            handle.DrawCircle(p, 11 * UIScale, colour.WithAlpha(.12f));
            handle.DrawLine(p - direction * 25 * UIScale, p, colour.WithAlpha(.35f));
            handle.DrawLine(p + direction * 6 * UIScale, p - direction * 4 * UIScale + normal * 4 * UIScale, colour);
            handle.DrawLine(p + direction * 6 * UIScale, p - direction * 4 * UIScale - normal * 4 * UIScale, colour);
            handle.DrawLine(p - direction * 4 * UIScale + normal * 4 * UIScale, p - direction * 4 * UIScale - normal * 4 * UIScale, colour);
        }
        if (OverviewDots && _model.PlayerFlight != Guid.Empty && PlayerWorldPosition() is { } travelling)
        {
            var point = ScreenPosition(travelling);
            handle.DrawCircle(point, 16 * UIScale, PlayerColor.WithAlpha(.22f));
            handle.DrawCircle(point, 16 * UIScale, PlayerColor, false);
        }
        if (VesselPosition is { } vessel)
        {
            var point = ScreenPosition(vessel); handle.DrawCircle(point, 7 * UIScale, Cyan.WithAlpha(.2f));
            handle.DrawLine(point + new Vector2(0, -8) * UIScale, point + new Vector2(6, 5) * UIScale, Cyan);
            handle.DrawLine(point + new Vector2(0, -8) * UIScale, point + new Vector2(-6, 5) * UIScale, Cyan);
            handle.DrawLine(point + new Vector2(-6, 5) * UIScale, point + new Vector2(6, 5) * UIScale, Cyan);
        }
        handle.DrawString(_font, new Vector2(12, PixelHeight - 12), "СЕКТОР / ТОРГОВЫЕ ПУТИ", UIScale * .8f, Muted);
    }
    private void DrawFog(DrawingHandleScreen handle)
    {
        if (_model.Systems.Count == 0) return;
        if (_fogTexture == null || _fogRevision != _model.Revision || _fogShowUnknown != ShowUnknown)
        {
            var shown = _model.Systems.Where(n => IsSystemVisible(n.Id) && (ShowUnknown || n.Known)).ToArray();
            _fogRevision = _model.Revision; _fogShowUnknown = ShowUnknown;
            if (_fogTexture == null || _fogSeed != _model.Seed || !_fogVisible.SetEquals(shown.Select(n => n.Id)))
            {
                _fogTexture?.Dispose(); _fogSeed = _model.Seed;
                _fogVisible.Clear(); foreach (var node in shown) _fogVisible.Add(node.Id);
                _fogWorld = new Box2(_model.Systems.Min(n => n.Position.X) - 1200, _model.Systems.Min(n => n.Position.Y) - 1200,
                    _model.Systems.Max(n => n.Position.X) + 1200, _model.Systems.Max(n => n.Position.Y) + 1200);
                using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(256, 256);
                var centers = shown.Select(n => n.Position).ToArray();
                for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
                {
                    var point = new Vector2(_fogWorld.Left + (x + .5f) * _fogWorld.Width / 256,
                        _fogWorld.Top - (y + .5f) * _fogWorld.Height / 256);
                    var alpha = (byte)(VoyageFogMask.Opacity(point, centers) * 240);
                    image[x, y] = new(6, 18, 24, alpha);
                }
                _fogTexture = (OwnedTexture)Texture.LoadFromImage(image, "sector-radar-fog", new TextureLoadParameters
                { Srgb = true, SampleParameters = new TextureSampleParameters { Filter = true } });
            }
        }
        var rectangle = new UIBox2(ScreenPosition(new(_fogWorld.Left, _fogWorld.Top)), ScreenPosition(new(_fogWorld.Right, _fogWorld.Bottom)));
        var dark = Color.FromHex("#061218").WithAlpha(240 / 255f);
        var left = Math.Clamp(rectangle.Left, 0, PixelWidth); var right = Math.Clamp(rectangle.Right, 0, PixelWidth);
        var top = Math.Clamp(rectangle.Top, 0, PixelHeight); var bottom = Math.Clamp(rectangle.Bottom, 0, PixelHeight);
        if (left > 0) handle.DrawRect(new UIBox2(0, 0, left, PixelHeight), dark);
        if (right < PixelWidth) handle.DrawRect(new UIBox2(right, 0, PixelWidth, PixelHeight), dark);
        if (right > left && top > 0) handle.DrawRect(new UIBox2(left, 0, right, top), dark);
        if (right > left && bottom < PixelHeight) handle.DrawRect(new UIBox2(left, bottom, right, PixelHeight), dark);
        handle.DrawTextureRect(_fogTexture!, rectangle);
    }

    private static Color FlightColor(VoyageFlight flight) => !flight.IsLocal
        ? Color.FromHex("#9AA5AE") : flight.Survey ? Color.FromHex("#BCACFF") : Cyan;

    private void DrawFlightRoute(DrawingHandleScreen handle, VoyageFlight flight)
    {
        var colour = FlightColor(flight);
        for (var i = 1; i < flight.Stops.Length; i++)
        {
            var from = _model.System(flight.Stops[i - 1]);
            var to = _model.System(flight.Stops[i]);
            if (!IsSystemVisible(from.Id) || !IsSystemVisible(to.Id) || !ShowUnknown && (!from.Known || !to.Known)) continue;
            var start = ScreenPosition(from.Position); var end = ScreenPosition(to.Position);
            var distance = Vector2.Distance(start, end);
            if (distance < 1) continue;
            var direction = (end - start) / distance;
            var normal = new Vector2(-direction.Y, direction.X);
            handle.DrawLine(start, end, colour.WithAlpha(.8f));
            handle.DrawLine(start + normal * 2 * UIScale, end + normal * 2 * UIScale, colour.WithAlpha(.18f));
            handle.DrawLine(start - normal * 2 * UIScale, end - normal * 2 * UIScale, colour.WithAlpha(.18f));
            var spacing = 48 * UIScale;
            var phase = (float)(_model.Clock * 2 * UIScale % spacing);
            for (var at = phase; at < distance; at += spacing)
            {
                var tip = start + direction * at;
                handle.DrawLine(tip, tip - direction * 5 * UIScale + normal * 3 * UIScale, colour.WithAlpha(.75f));
                handle.DrawLine(tip, tip - direction * 5 * UIScale - normal * 3 * UIScale, colour.WithAlpha(.75f));
            }
        }
    }
    public Vector2? PlayerWorldPosition()
    {
        if (_model.PlayerFlight != Guid.Empty && _model.ServerFlights.FirstOrDefault(f => f.Id == _model.PlayerFlight) is { } live)
        {
            var returning = live.Delivered || _model.Clock >= live.Arrive;
            var flight = new VoyageFlight(live.Name, returning ? live.Stops.Reverse().ToArray() : live.Stops,
                returning ? live.Arrive : live.Depart, Math.Max(.001, returning ? live.Return - live.Arrive : live.Arrive - live.Depart), Mine: true);
            return _model.Position(flight);
        }
        return _model.Systems.FirstOrDefault(s => s.Id == _model.PlayerSector)?.Position;
    }
    private static void Dash(DrawingHandleScreen handle, Vector2 start, Vector2 end, Color colour)
    {
        var distance = Vector2.Distance(start, end);
        for (var at = 0f; at < distance; at += 13)
            handle.DrawLine(Vector2.Lerp(start, end, at / distance), Vector2.Lerp(start, end, Math.Min(at + 6, distance) / distance), colour);
    }
    [Obsolete("Controls should only be removed from UI tree instead of being disposed")]
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fogTexture?.Dispose(); _surface.Dispose(); foreach (var portrait in _portraits.Values) portrait?.Dispose(); }
        base.Dispose(disposing);
    }
}
