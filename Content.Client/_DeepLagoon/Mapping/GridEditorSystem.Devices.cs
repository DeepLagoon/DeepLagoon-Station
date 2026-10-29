using System.Linq;
using Content.Client._DeepLagoon.WebUI;
using Robust.Shared.Prototypes;

namespace Content.Client._DeepLagoon.Mapping;

public sealed partial class GridEditorSystem
{
    private IEnumerable<TguiData> RouteDevices()
    {
        foreach (var prototype in _prototypes.EnumeratePrototypes<EntityPrototype>().Where(proto => !proto.Abstract))
        {
            var ancestors = _prototypes.EnumerateParents(prototype, true).Select(parent => parent.ID).ToHashSet();
            var disposal = ancestors.Contains("DisposalUnit") || ancestors.Contains("MailingUnit");
            var gas = new[] { "GasVentPump", "GasVentScrubber", "GasOutletInjector", "GasPassiveVent", "GasDualPortVentPump" }.Any(ancestors.Contains);
            if (!disposal && !gas) continue;
            yield return new TguiData().String("id", prototype.ID).String("name", $"{prototype.Name} [{prototype.ID}]")
                .String("kind", disposal ? "disposal" : "gas").Number("ports", ancestors.Contains("GasDualPortVentPump") ? 3 : 2);
        }
    }
}
