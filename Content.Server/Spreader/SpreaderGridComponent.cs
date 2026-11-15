// Mono - file changed
using Content.Shared.Spreader;
using Robust.Shared.Prototypes;

namespace Content.Server.Spreader;

[RegisterComponent]
public sealed partial class SpreaderGridComponent : Component
{
    [DataField]
    public float UpdateAccumulator = 0f;

    [DataField]
    public float UpdateSpacing = 1f;

    // Old maps contain empty queues. Accept those on load, then rebuild from active spreaders.
    [DataField(readOnly: true)]
    public Dictionary<ProtoId<EdgeSpreaderPrototype>, Queue<Entity<EdgeSpreaderComponent>>> SpreadQueues = new();
}
