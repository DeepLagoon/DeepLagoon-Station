using System.Globalization;
using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Content.Client.Players.PlayTimeTracking;
using Content.Shared.Ghost.Roles;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client.UserInterface.Systems.Ghost.Controls.Roles;

public sealed partial class GhostRolesWindow
{
    private GhostRoleTguiHost? _tguiHost;
    private readonly TguiPanel _tgui = new(inheritChatAppearance: false, showWindowControls: false);
    private GhostRoleInfo[] _tguiRoles = [];
    private readonly Dictionary<uint, bool> _access = new();
    private readonly IGameTiming _timing = IoCManager.Resolve<IGameTiming>();
    private float _tguiRefresh;
    private float _readyWait;
    public event Action<uint[]>? OnPreviewSubscription;

    private void InitializeTgui()
    {
        RoleScroll.Visible = NoRolesMessage.Visible = false;
        _tguiHost = new GhostRoleTguiHost(_tgui);
        _tguiHost.SubscriptionChanged += ids => OnPreviewSubscription?.Invoke(ids);
        ContentsContainer.AddChild(_tguiHost);
        _tgui.OnClose += Close;
        _tgui.OnAction += OnTguiAction;
        _tgui.Web.Failed += RestoreClassic;
        _tgui.SetNativeFallback(RestoreClassic);
        _tgui.SetTguiRestore(() =>
        {
            _readyWait = 0;
            RoleScroll.Visible = NoRolesMessage.Visible = false;
            _tguiHost.Visible = true;
            _tguiHost.SetRoles(_tguiRoles);
            PublishTgui();
        });
        PublishTgui();
    }

    public void SetTguiRoles(GhostRoleInfo[] roles)
    {
        _tguiRoles = roles;
        if (!_tgui.UsingNative) _tguiHost?.SetRoles(roles);
        PublishTgui();
    }

    private void RestoreClassic()
    {
        if (_tguiHost == null) return;
        _tguiHost.ClearPreviews();
        _tguiHost.Visible = false;
        RoleScroll.Visible = true;
        NoRolesMessage.Visible = _tguiRoles.Length == 0;
        OnPreviewSubscription?.Invoke([]);
    }

    private void PublishTgui()
    {
        if (_tguiHost == null) return;
        var requirements = IoCManager.Resolve<JobRequirementsManager>();
        var prototypes = IoCManager.Resolve<IPrototypeManager>();
        _access.Clear();
        var groups = new List<TguiData>();
        foreach (var group in _tguiRoles.GroupBy(role => (role.Name, role.Description, role.Requirements, role.Prototype)))
        {
            var allowed = requirements.CheckRoleRequirements(group.Key.Requirements, null, out var reason);
            if (allowed && prototypes.TryIndex(group.Key.Prototype, out var prototype) && !requirements.IsAllowed(prototype, out reason))
                allowed = false;
            var entries = new List<TguiData>();
            foreach (var role in group)
            {
                _access[role.Identifier] = allowed;
                entries.Add(new TguiData().String("id", role.Identifier.ToString(CultureInfo.InvariantCulture))
                    .Number("kind", (int) role.Kind).Bool("allowed", allowed)
                    .String("reason", reason?.ToString()).String("request", RequestLabel(role))
                    .String("preview", role.PreviewEntity != null ? "entity" : role.PreviewPrototype != null ? "prototype" : "unknown"));
            }
            groups.Add(new TguiData().String("name", group.Key.Name)
                .String("description", FormattedMessage.FromMarkupPermissive(group.Key.Description).ToString())
                .Array("roles", entries));
        }
        var labels = new TguiData()
            .String("empty", Loc.GetString("ghost-roles-window-no-roles-available-label"))
            .String("follow", Loc.GetString("ghost-roles-window-follow-role-button"));
        foreach (var key in new[] { "available", "groups", "roles", "locked", "preview-loading", "preview-spawner", "preview-unknown", "select", "instance" })
            labels.String(key, Loc.GetString("dl-ghost-roles-" + key));
        _tgui.SetState("GhostRoles", new TguiData().Array("groups", groups).Object("labels", labels).ToString(), Title ?? Loc.GetString("ghost-roles-window-title"));
    }

    private string RequestLabel(GhostRoleInfo role)
    {
        var key = role.Kind switch
        {
            GhostRoleKind.FirstComeFirstServe => "ghost-roles-window-request-role-button",
            GhostRoleKind.RaffleReady => "ghost-roles-window-join-raffle-button",
            GhostRoleKind.RaffleInProgress => "ghost-roles-window-raffle-in-progress-button",
            GhostRoleKind.RaffleJoined => "ghost-roles-window-leave-raffle-button",
            _ => throw new ArgumentOutOfRangeException(nameof(role.Kind))
        };
        var remaining = role.RaffleEndTime > _timing.CurTime ? role.RaffleEndTime - _timing.CurTime : TimeSpan.Zero;
        return Loc.GetString(key, ("time", $"{remaining.Minutes:0}:{remaining.Seconds:00}"), ("players", role.RafflePlayerCount));
    }

    private void OnTguiAction(string action, string payload)
    {
        if (_tguiHost == null || !TguiActionData.TryParse(payload, out var args) || args == null) return;
        if (action == "preview-layout")
        {
            if (args.TryFloat("width", out var width) && args.TryFloat("height", out var height))
                _tguiHost.SetLayout(args.String("layout") ?? "", width, height);
            return;
        }
        if (!uint.TryParse(args.String("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return;
        var found = _tguiRoles.FirstOrDefault(role => role.Identifier == id);
        if (!_access.ContainsKey(id)) return;
        if (action == "request" && _access[id]) OnRoleRequestButtonClicked?.Invoke(found);
        else if (action == "follow") OnRoleFollow?.Invoke(found);
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        if (_tguiHost == null || _tgui.UsingNative || !VisibleInTree) return;
        if (!_tgui.Web.IsReady)
        {
            _readyWait += args.DeltaSeconds;
            if (_readyWait >= 8f) { UserInterfaceManager.DeferAction(_tgui.SwitchToNative); return; }
        }
        else _readyWait = 0;
        _tguiRefresh += args.DeltaSeconds;
        if (_tguiRefresh < 0.25f) return;
        _tguiRefresh = 0;
        PublishTgui();
    }
}
