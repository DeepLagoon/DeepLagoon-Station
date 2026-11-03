using System.Globalization;
using Content.Server.Administration.Managers;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Robust.Server.Console;
using Robust.Server.GameObjects;
using Robust.Server.ViewVariables;
using Robust.Shared.Placement;
using Robust.Shared.Player;

namespace Content.Server._DeepLagoon.Administration;

public sealed class AdminProvenanceSystem : EntitySystem
{
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IServerConsoleHost _console = default!;
    [Dependency] private ISharedPlayerManager _players = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlacementEntityEvent>(OnPlacement);
        SubscribeLocalEvent<ViewVariablesModifiedEvent>(OnModified);
        SubscribeLocalEvent<AdminProvenanceComponent, ExaminedEvent>(OnExamined);
        EntityManager.EntityInitialized += OnInitialized;
    }

    public override void Shutdown()
    {
        EntityManager.EntityInitialized -= OnInitialized;
        base.Shutdown();
    }

    private void OnPlacement(PlacementEntityEvent ev)
    {
        if (ev.PlacementEventAction == PlacementEventAction.Create &&
            _players.TryGetSessionById(ev.PlacerNetUserId, out var player))
            RecordSpawn(ev.EditedEntity, player);
    }

    private void OnInitialized(Entity<MetaDataComponent> entity)
    {
        // The engine restores this context in finally, including nested commands and sudo.
        if (_console.ExecutingSession is { } player)
            RecordSpawn(entity.Owner, player);
    }

    private void RecordSpawn(EntityUid uid, ICommonSession player)
    {
        if (!_admins.IsAdmin(player) || TerminatingOrDeleted(uid))
            return;

        var provenance = EnsureComp<AdminProvenanceComponent>(uid);
        if (provenance.SpawnedAt != null)
            return;

        provenance.SpawnedBy = player.UserId;
        provenance.SpawnedByName = player.Name;
        provenance.SpawnedAt = DateTime.UtcNow;
    }

    private void OnModified(ViewVariablesModifiedEvent ev)
    {
        if (TerminatingOrDeleted(ev.Entity) ||
            !_players.TryGetSessionById(ev.User, out var player) ||
            !_admins.IsAdmin(player))
            return;

        var provenance = EnsureComp<AdminProvenanceComponent>(ev.Entity);
        provenance.ModifiedBy = player.UserId;
        provenance.ModifiedByName = player.Name;
        provenance.ModifiedAt = DateTime.UtcNow;
    }

    private void OnExamined(EntityUid uid, AdminProvenanceComponent component, ExaminedEvent args)
    {
        if (!TryComp<GhostComponent>(args.Examiner, out var ghost) || !ghost.CanGhostInteract ||
            !TryComp<ActorComponent>(args.Examiner, out var actor) ||
            !_admins.IsAdmin(actor.PlayerSession))
            return;

        // Plain text prevents account names from injecting examine markup.
        if (component.SpawnedAt is { } spawnedAt && component.SpawnedByName is { } spawnedBy)
            args.PushText(Loc.GetString("admin-provenance-spawned",
                ("admin", spawnedBy), ("time", FormatTime(spawnedAt))));

        if (component.ModifiedAt is { } modifiedAt && component.ModifiedByName is { } modifiedBy)
            args.PushText(Loc.GetString("admin-provenance-modified",
                ("admin", modifiedBy), ("time", FormatTime(modifiedAt))));
    }

    private static string FormatTime(DateTime time)
        => time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
