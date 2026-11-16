using Content.Shared.Shuttles.Components;
using Robust.Client.Graphics;

namespace Content.Client.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    [Dependency] private IOverlayManager _overlays = default!;

    public override void Initialize()
    {
        base.Initialize();
        InitializeEmergency();
        _overlays.AddOverlay(new FtlArrivalOverlay());
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlays.RemoveOverlay<FtlArrivalOverlay>();
    }

    public string? GetRadarIFFLabel(EntityUid gridUid, bool self = false, IFFComponent? component = null)
    {
        var label = GetIFFLabel(gridUid, self, component);
        if (string.IsNullOrEmpty(label) || !HasComp<ShuttleAutopilotActiveComponent>(gridUid))
            return label;

        // The company and distance use a separate line on the radar.
        var newline = label.IndexOf('\n');
        var name = newline < 0 ? label : label[..newline];
        var markedName = Loc.GetString("shuttle-radar-autopilot-label", ("name", name));
        return newline < 0 ? markedName : markedName + label[newline..];
    }
}
