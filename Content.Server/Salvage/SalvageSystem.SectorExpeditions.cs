using Content.Server._DeepLagoon.Voyages;
using Content.Shared.Salvage.Expeditions;
using Content.Shared.Shuttles.Components;
namespace Content.Server.Salvage;
public sealed partial class SalvageSystem
{
    /// <summary>Runs the regular generation, objectives, loot, rewards and departure hooks from an orbit.</summary>
    public void BeginSectorPlanet(EntityUid ship, EntityUid console, SalvageMissionParams parameters)
    {
        var data = EnsureComp<SalvageExpeditionDataComponent>(ship);
        if (data.Claimed || HasComp<FTLComponent>(ship) || IsExpeditionShuttleDocked(ship))
            throw new InvalidOperationException("Шаттл занят, находится в БСС или пристыкован");
        data.Enabled = true; data.Missions.Clear(); data.Missions[parameters.Index] = parameters;
        data.ActiveMission = parameters.Index; data.NextOffer = _timing.CurTime + TimeSpan.FromHours(2);
        SpawnMission(parameters, ship, console, null); UpdateConsoles(ship, data);
    }
}
