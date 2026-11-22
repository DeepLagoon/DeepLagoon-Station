using System.Linq;
using Content.Server.Database;
using Content.Server._DeepLagoon.ShuttleParking;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Research.Components;

namespace Content.Server._DeepLagoon.CrashRecovery;

public sealed partial class CrashRecoverySystem
{
    private readonly List<EntityUid> _researchServers = new();
    private readonly Dictionary<EntityUid, RecoveryAsset> _researchBalances = new();
    private readonly HashSet<EntityUid> _persistentResearch = new();
    private readonly Dictionary<EntityUid, RecoveryAsset> _researchPrints = new();
    private int _researchCursor;

    public void RegisterOwnedShip(EntityUid ship, EntityUid buyer)
    {
        if (!_enabled || !TryComp<RecoveryBodyComponent>(buyer, out var body)) return;
        var owner = EnsureComp<RecoveryShipOwnerComponent>(ship);
        owner.User = body.User; owner.Profile = body.Profile;
    }

    public void ResearchDiskUploaded(EntityUid disk, EntityUid server)
    {
        if (!_open) return;
        SaveResearch(server);
        if (!_assets.TryGetValue(disk, out var old)) return;
        var consumed = old with { Revision = long.MaxValue, Location = RecoveryLocation.Destroyed, Data = "" };
        _assets[disk] = consumed;
        if (_researchBalances.TryGetValue(server, out var balance)) StageAtomic(consumed, balance);
        else StageAtomic(consumed);
    }

    public void ResearchPrintStarted(EntityUid printer, EntityUid server, int nominal)
    {
        if (!_open || nominal <= 0) return;
        SaveResearch(server);
        if (!_researchBalances.TryGetValue(server, out var balance)) return;
        var disk = new RecoveryAsset(Guid.NewGuid(), ++_revision, balance.User, balance.Profile, 0, "ResearchDisk",
            RecoveryLocation.Carried, null, [], "", "research", nominal);
        _researchPrints[printer] = disk;
        StageAtomic(balance, disk);
    }

    public void ResearchPrintCompleted(EntityUid printer, EntityUid disk)
    {
        if (!_open || !_researchPrints.Remove(printer, out var pending)) return;
        EnsureComp<RecoveryIdentityComponent>(disk).Identity = pending.Identity;
        var asset = pending with { Revision = ++_revision, Kind = "item", Amount = 0, YamlId = ++_nextYaml,
            Prototype = MetaData(disk).EntityPrototype?.ID ?? "ResearchDisk", Location = RecoveryLocation.Loose };
        _assets[disk] = asset;
        Track(disk);
        _entitiesByIdentity[asset.Identity] = disk;
        Stage(asset);
        _discover.Enqueue(disk, 65536);
    }

    public void ResearchPrintCancelled(EntityUid printer)
    {
        if (!_open || !_researchPrints.Remove(printer, out var pending)) return;
        Stage(pending with { Revision = ++_revision, Location = RecoveryLocation.Destroyed });
    }

    public void ResearchServerRemoved(EntityUid uid)
    {
        if (_open && !_closing && _researchBalances.TryGetValue(uid, out var prior) && prior.Location != RecoveryLocation.Persistent)
            Stage(prior with { Revision = ++_revision, Location = RecoveryLocation.Destroyed });
    }

    public void ResearchServerStarted(EntityUid uid)
    {
        if (_enabled && !_researchServers.Contains(uid)) _researchServers.Add(uid);
    }

    /// <summary>Called at the central balance mutation, including spending and disks.</summary>
    public void ResearchChanged(EntityUid server)
    {
        if (!_open || _closing) return;
        if (!_researchServers.Contains(server)) _researchServers.Add(server);
        SaveResearch(server);
    }

    private void PollResearch()
    {
        if (_researchServers.Count == 0) return;
        if (_researchCursor >= _researchServers.Count) _researchCursor = 0;
        SaveResearch(_researchServers[_researchCursor++]);
    }

    private void SaveResearch(EntityUid uid)
    {
        if (TerminatingOrDeleted(uid) || !TryComp<ResearchServerComponent>(uid, out var research)) return;
        var grid = Transform(uid).GridUid;
        if (grid is not { } ship || !TryComp<ShipOwnershipComponent>(ship, out var ownership)) return;
        // Ownership is captured on the ship, never inferred from who uses a research console.
        int ownerProfile;
        _researchBalances.TryGetValue(uid, out var prior);
        if (TryComp<RecoveryShipOwnerComponent>(ship, out var stable) && stable.User == ownership.OwnerUserId.UserId)
            ownerProfile = stable.Profile;
        else if (prior != null && prior.User == ownership.OwnerUserId.UserId)
            ownerProfile = prior.Profile;
        else
        {
            var profiles = _bodies.Where(b => !TerminatingOrDeleted(b) && TryComp<RecoveryBodyComponent>(b, out var body)
                                            && body.User == ownership.OwnerUserId.UserId)
                .Select(b => Comp<RecoveryBodyComponent>(b).Profile).Distinct().Take(2).ToArray();
            if (profiles.Length != 1) return; // ambiguous character ownership must not pay the wrong character
            ownerProfile = profiles[0];
        }
        var id = EnsureComp<RecoveryIdentityComponent>(uid).Identity;
        var location = _persistentResearch.Contains(uid) ? RecoveryLocation.Persistent : RecoveryLocation.Carried;
        var points = Math.Max(0, research.Points);
        if (prior != null && prior.Amount == points && prior.User == ownership.OwnerUserId.UserId && prior.Location == location) return;
        var asset = new RecoveryAsset(id, ++_revision, ownership.OwnerUserId.UserId, ownerProfile, 0, "ResearchDisk",
            location, null, [], "", "research", points);
        _researchBalances[uid] = asset;
        Stage(asset);
    }

    /// <summary>Invoke only after the parking snapshot's durable commit, before source deletion.</summary>
    public void ResearchPersisted(IEnumerable<EntityUid> entities)
    {
        if (!_open) return;
        foreach (var uid in entities)
            if (HasComp<ResearchServerComponent>(uid))
            {
                _persistentResearch.Add(uid);
                SaveResearch(uid);
            }
    }

    private void ResetResearch()
    {
        _researchServers.Clear(); _researchBalances.Clear(); _persistentResearch.Clear(); _researchPrints.Clear(); _researchCursor = 0;
    }
}
