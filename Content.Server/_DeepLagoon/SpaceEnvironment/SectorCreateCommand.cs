using Content.Server.Administration;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server._DeepLagoon.SpaceEnvironment;

[AdminCommand(AdminFlags.Mapping)]
public sealed class SectorCreateCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "dl_sector_create";
    public string Description => "Create/load a persistent generated sector without moving ships or players.";
    public string Help => "dl_sector_create <stable-sector-id> <uint-seed> <sectorProfile>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 3 || !uint.TryParse(args[1], out var seed))
        {
            shell.WriteError(Help);
            return;
        }
        try
        {
            var map = _entities.System<SectorSystem>().Create(args[0], seed, args[2]);
            shell.WriteLine($"Sector {args[0]} loaded on map {_entities.GetComponent<TransformComponent>(map).MapID}. Existing manifests keep their original seed/profile.");
        }
        catch (Exception e) when (e is ArgumentException or System.IO.InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            shell.WriteError(e.Message);
        }
    }
}

[AdminCommand(AdminFlags.Mapping)]
public sealed class SectorUnloadCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = default!;
    public string Command => "dl_sector_unload";
    public string Description => "Unload an empty generated sector; keep its persistent manifest and discoveries.";
    public string Help => "dl_sector_unload <map-id>";
    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out var id)) { shell.WriteError(Help); return; }
        var maps = _entities.System<SharedMapSystem>();
        if (!maps.TryGetMap(new MapId(id), out var map)
            || !_entities.System<SectorSystem>().TryUnload(map.Value))
        {
            shell.WriteError("Sector not found or still contains a grid/player. No ships or players were deleted.");
            return;
        }
        shell.WriteLine("Sector unloaded. Its manifest and survey journal are retained.");
    }
}
