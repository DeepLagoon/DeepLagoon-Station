using System.Linq;
using Content.Server.Administration;
using Content.Server.PsionicsRecords.Systems;
using Content.Server.StationRecords.Systems;
using Content.Shared.Administration;
using Content.Shared.Abilities.Psionics;
using Content.Shared.IdentityManagement;
using Content.Shared.Psionics;
using Content.Shared.Psionics.Components;
using Content.Shared.Preferences.Loadouts;
using Content.Shared.Roles;
using Content.Shared.Station;
using Content.Shared.StationRecords;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Prototypes;

namespace Content.Server._DeepLagoon.Mantis;

[AdminCommand(AdminFlags.Debug)]
public sealed partial class SetPsionicStatusCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;

    public string Command => "setpsionicstatus";

    public string Description => "Set the psionics registry status of an online player.";

    public string Help => "setpsionicstatus <player> <none|suspected|registered|abusing> [reason]";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2)
        {
            shell.WriteError(Help);
            return;
        }

        var session = _players.Sessions.FirstOrDefault(p =>
            p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));

        if (session?.AttachedEntity is not { Valid: true } subject)
        {
            shell.WriteError("Player not found or has no entity.");
            return;
        }

        if (!Enum.TryParse<PsionicsStatus>(args[1], true, out var status))
        {
            shell.WriteError("Unknown status. Expected none, suspected, registered or abusing.");
            return;
        }

        if (!TryGetKey(subject, out var key))
        {
            shell.WriteError("That player has no station record yet.");
            return;
        }

        var reason = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;

        if (!_entities.System<PsionicsRecordsSystem>().TryChangeStatus(key, status, reason))
        {
            shell.WriteError("Status unchanged; the record already has that value or is missing.");
            return;
        }

        shell.WriteLine($"Psionics status of {session.Name} set to {status}.");
    }

    private bool TryGetKey(EntityUid subject, out StationRecordKey key)
    {
        key = default;

        var xform = _entities.GetComponent<TransformComponent>(subject);
        var station = _entities.System<SharedStationSystem>().GetStationInMap(xform.MapID);
        if (station is not { } stationUid)
            return false;

        var name = Identity.Name(subject, _entities);
        if (_entities.System<StationRecordsSystem>().GetRecordByName(stationUid, name) is not { } id)
            return false;

        key = new StationRecordKey(id, stationUid);
        return true;
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class CheckPsionicSignatureCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPlayerManager _players = default!;

    public string Command => "checksignature";

    public string Description => "Report whether an online player reads as a spectrometer signature.";

    public string Help => "checksignature <player>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError(Help);
            return;
        }

        var session = _players.Sessions.FirstOrDefault(p =>
            p.Name.Equals(args[0], StringComparison.OrdinalIgnoreCase));

        if (session?.AttachedEntity is not { Valid: true } subject)
        {
            shell.WriteError("Player not found or has no entity.");
            return;
        }

        var psionic = _entities.HasComponent<PsionicComponent>(subject);
        var insulated = _entities.HasComponent<PsionicInsulationComponent>(subject);
        var disabled = _entities.HasComponent<PsionicsDisabledComponent>(subject);
        var hasRecord = _entities.HasComponent<PsionicsRecordComponent>(subject);

        shell.WriteLine($"{session.Name}: psionic={psionic}, insulated={insulated}, " +
                        $"disabled={disabled}, hasRecord={hasRecord}");

        if (insulated)
            shell.WriteLine("Insulated subjects are invisible to the spectrometer.");

        if (!psionic)
            shell.WriteLine("Without PsionicComponent the subject never reads as a signature.");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed partial class MantisLoadoutReportCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public string Command => "mantisloadout";

    public string Description => "Report the resolved loadout and starting gear of the Mantis postings.";

    public string Help => "mantisloadout [Mantis|TsfMantis] - omit the job to report both";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {

        var jobs = args.Length == 0 ? new[] { "Mantis", "TsfMantis" } : args;

        foreach (var jobId in jobs)
            Report(shell, jobId);
    }

    private void Report(IConsoleShell shell, string jobId)
    {
        var roleId = "Job" + jobId;

        if (!_prototypes.HasIndex<JobPrototype>(jobId))
        {
            shell.WriteError($"No job prototype named {jobId}.");
            return;
        }

        var job = _prototypes.Index<JobPrototype>(jobId);
        shell.WriteLine($"Job {jobId}: startingGear={job.StartingGear?.ToString() ?? "<none>"}, setPreference={job.SetPreference}");

        if (!_prototypes.TryIndex<RoleLoadoutPrototype>(roleId, out var role))
        {
            shell.WriteError($"No role loadout {roleId}: the job cannot be customised and its kit has nowhere to go.");
            return;
        }

        shell.WriteLine($"Role loadout {roleId}: {role.Groups.Count} groups, points={role.Points?.ToString() ?? "<none>"}");

        foreach (var groupId in role.Groups)
        {
            if (!_prototypes.TryIndex<LoadoutGroupPrototype>(groupId, out var group))
            {
                shell.WriteError($"  {groupId}: MISSING");
                continue;
            }

            var free = group.Fallbacks
                .Where(id => _prototypes.TryIndex(id, out LoadoutPrototype? l) && l.IsFree)
                .ToList();

            var status = free.Count > 0 || group.MinLimit == 0 ? "ok" : "NO FREE FALLBACK";

            shell.WriteLine($"  {groupId} (min={group.MinLimit} max={group.MaxLimit} hidden={group.Hidden}): " +
                            $"{group.Loadouts.Count} options, free fallbacks=[{string.Join(',', free)}] {status}");
        }
    }
}
