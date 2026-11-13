using Content.Server.Power.Components;
using Content.Shared.Examine;
using Content.Shared.Research.Components;
using Content.Shared.Research.TechnologyDisk.Components;
using Content.Shared.Research.TechnologyDisk.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Timing;

namespace Content.Server.Research.Disk;

public sealed partial class ResearchDiskCollectorSystem : EntitySystem
{
    [Dependency] private ResearchDiskSystem _researchDisks = default!;
    [Dependency] private TechnologyDiskSystem _techDisks = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly HashSet<EntityUid> _nearby = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<ResearchDiskCollectorComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<ResearchDiskCollectorComponent, ExaminedEvent>(OnExamined);
    }

    private void OnVerbs(Entity<ResearchDiskCollectorComponent> server, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("magnet-pickup-component-toggle-verb"),
            Act = () => server.Comp.Enabled = !server.Comp.Enabled,
        });
    }

    private void OnExamined(Entity<ResearchDiskCollectorComponent> server, ref ExaminedEvent args)
    {
        var state = Loc.GetString(server.Comp.Enabled ? "magnet-pickup-component-magnet-on" : "magnet-pickup-component-magnet-off");
        args.PushMarkup(Loc.GetString("magnet-pickup-component-on-examine-main", ("stateText", state)));
    }

    public override void Update(float frameTime)
    {
        var now = _timing.CurTime;
        var servers = EntityQueryEnumerator<ResearchDiskCollectorComponent, ResearchServerComponent, ApcPowerReceiverComponent>();
        while (servers.MoveNext(out var uid, out var collector, out var server, out var power))
        {
            if (!collector.Enabled || !power.Powered || collector.Range <= 0 || collector.Interval <= TimeSpan.Zero ||
                collector.BatchSize <= 0 || now < collector.NextScan)
            {
                continue;
            }

            collector.NextScan = now + collector.Interval;
            _nearby.Clear();
            _lookup.GetEntitiesInRange(uid, collector.Range, _nearby, LookupFlags.Dynamic | LookupFlags.Sundries);
            var accepted = 0;
            foreach (var candidate in _nearby)
            {
                if (accepted >= collector.BatchSize)
                    break;

                if (!TryComp<PhysicsComponent>(candidate, out var physics) ||
                    physics.BodyStatus != BodyStatus.OnGround || _containers.IsEntityInContainer(candidate))
                {
                    continue;
                }

                if (TryComp<ResearchDiskComponent>(candidate, out var points))
                    accepted += _researchDisks.TryUpload((candidate, points), (uid, server), null) ? 1 : 0;
                else if (TryComp<TechnologyDiskComponent>(candidate, out var technology))
                    accepted += _techDisks.TryUpload((candidate, technology), uid, null) ? 1 : 0;
            }
        }
    }
}
