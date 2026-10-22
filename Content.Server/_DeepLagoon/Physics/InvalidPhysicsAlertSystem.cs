using Content.Server.Chat.Managers;
using Robust.Shared.Physics.Events;

namespace Content.Server._DeepLagoon.Physics;

/// <summary>
/// Reports the engine's server-side fixture quarantine to administrators. The engine removes unsafe
/// shapes and queues the entity for deletion before notifying us; chat must not run inside physics.
/// </summary>
public sealed partial class InvalidPhysicsAlertSystem : EntitySystem
{
    [Dependency] private IChatManager _chat = default!;

    private readonly Dictionary<(string Prototype, string Fixture, string Reason), PendingAlert> _pending = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<InvalidPhysicsFixtureEvent>(OnInvalidFixture);
    }

    private void OnInvalidFixture(ref InvalidPhysicsFixtureEvent ev)
    {
        var prototype = MetaData(ev.Entity).EntityPrototype?.ID ?? "<no prototype>";
        var key = (prototype, ev.FixtureId, ev.Reason);
        if (_pending.TryGetValue(key, out var alert))
        {
            alert.Count++;
            return;
        }

        // Capture the identity now: the entity can already be deleted by the following update.
        _pending.Add(key, new PendingAlert(ToPrettyString(ev.Entity)));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        foreach (var (key, alert) in _pending)
        {
            _chat.SendAdminAlert(Loc.GetString("dl-physics-invalid-fixture-alert",
                ("prototype", key.Prototype), ("fixture", key.Fixture), ("reason", key.Reason),
                ("count", alert.Count), ("entity", alert.Example)));
        }
        _pending.Clear();
    }

    private sealed class PendingAlert(string example)
    {
        public readonly string Example = example;
        public int Count = 1;
    }
}
